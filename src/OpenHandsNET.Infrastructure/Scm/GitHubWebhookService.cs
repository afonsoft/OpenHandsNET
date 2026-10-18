using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Contracts;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Application.Scm;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Scm;

/// <summary>
/// GitHub webhook pipeline (SPEC-52 RF-004): HMAC-SHA256
/// (<c>X-Hub-Signature-256</c>) → idempotency by <c>X-GitHub-Delivery</c> →
/// mention routing (<c>@openhandsnet</c> → start-task). O payload nunca é
/// executado — só extraído como contexto da mensagem inicial.
/// </summary>
public sealed class GitHubWebhookService(
    AppDbContext db,
    ISecretStore secrets,
    OpenHandsNET.Application.Conversations.IAppConversationService conversations,
    IAuditService audit,
    ILogger<GitHubWebhookService> logger) : IScmWebhookService
{
    /// <summary>Mention marker that triggers a conversation.</summary>
    public const string Mention = "@openhandsnet";

    /// <inheritdoc />
    public async Task<ScmWebhookResult> HandleGitHubAsync(
        string? signature256, string eventType, string? deliveryId,
        byte[] payload, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return ScmWebhookResult.InvalidSignature;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var installationId = root.TryGetProperty("installation", out var inst)
                && inst.TryGetProperty("id", out var iid)
                    ? iid.GetRawText() : null;
            var repository = root.TryGetProperty("repository", out var repo)
                && repo.TryGetProperty("full_name", out var fn)
                    ? fn.GetString() : null;

            var connection = await FindConnectionAsync(
                installationId, repository, ct);
            if (connection is null)
            {
                return ScmWebhookResult.NoConnection;
            }

            if (!await VerifySignatureAsync(
                    connection, signature256, payload, ct))
            {
                await audit.RecordAsync("scm.webhook_rejected", orgId: null,
                    targetType: "scm_connection", targetId: connection.Id.ToString(),
                    metadata: new { reason = "bad_signature", eventType },
                    cancellationToken: ct);
                return ScmWebhookResult.InvalidSignature;
            }

            if (await IsDuplicateAsync(deliveryId, payload, ct))
            {
                return ScmWebhookResult.Duplicate;
            }

            var mention = ParseMention(eventType, root, installationId);
            if (mention is null)
            {
                return ScmWebhookResult.Ignored;
            }

            var ownerUserId = await ResolveOwnerUserIdAsync(connection, ct);
            if (ownerUserId is null)
            {
                logger.LogWarning(
                    "scm mention without resolvable owner (conn {Id})",
                    connection.Id);
                return ScmWebhookResult.Ignored;
            }

            var prompt = BuildPrompt(mention, root);
            var task = await conversations.CreateForUserAsync(
                ownerUserId.Value, connection.OrgId,
                new CreateConversationRequest(
                    new InitialMessage("user", [prompt], Run: true),
                    mention.Repository,
                    AgentType: null, LlmModel: null, SandboxSpecId: null),
                ct);

            await audit.RecordAsync("scm.webhook_triggered",
                actorUserId: ownerUserId.Value.ToString(),
                orgId: connection.OrgId,
                targetType: "app_conversation",
                targetId: task.AppConversationId?.ToString(),
                metadata: new
                {
                    event_type = eventType,
                    repository = mention.Repository,
                    pr = mention.PullRequestNumber,
                },
                cancellationToken: ct);
            return ScmWebhookResult.Processed;
        }
    }

    private async Task<ScmConnection?> FindConnectionAsync(
        string? installationId, string? repository, CancellationToken ct)
    {
        var query = db.ScmConnections.IgnoreQueryFilters()
            .Where(c => c.Kind == "github");
        if (installationId is not null)
        {
            var byInstall = await query
                .FirstOrDefaultAsync(c => c.InstallationId == installationId, ct);
            if (byInstall is not null)
            {
                return ScmConnectionService.ToModel(byInstall);
            }
        }
        if (repository is not null)
        {
            var rows = await query.ToListAsync(ct);
            return rows.Select(ScmConnectionService.ToModel)
                .FirstOrDefault(c => c.Repos.Count == 0
                    || c.Repos.Contains(repository,
                        StringComparer.OrdinalIgnoreCase));
        }
        return null;
    }

    private async Task<bool> VerifySignatureAsync(
        ScmConnection connection, string? signature256,
        byte[] payload, CancellationToken ct)
    {
        if (connection.WebhookSecretRef is null || signature256 is null
            || !signature256.StartsWith("sha256=", StringComparison.Ordinal))
        {
            return false;
        }

        var secret = await secrets.ResolveAsync(
            "org", connection.OrgId?.ToString() ?? "",
            connection.WebhookSecretRef, ct);
        if (secret is null)
        {
            return false;
        }

        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(secret), payload));
        var presented = signature256["sha256=".Length..];
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(presented));
    }

    private async Task<bool> IsDuplicateAsync(
        string? deliveryId, byte[] payload, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(deliveryId))
        {
            return false;
        }

        var exists = await db.WebhookDeliveries.IgnoreQueryFilters()
            .AnyAsync(d => d.Kind == "github" && d.DeliveryId == deliveryId, ct);
        if (exists)
        {
            return true;
        }

        db.WebhookDeliveries.Add(new WebhookDeliveryRecord
        {
            Id = Guid.NewGuid(),
            Kind = "github",
            DeliveryId = deliveryId,
            PayloadSha = Convert.ToHexStringLower(
                SHA256.HashData(payload)),
            ProcessedAt = DateTimeOffset.UtcNow,
        });
        try
        {
            await db.SaveChangesAsync(ct);
            return false;
        }
        catch (DbUpdateException)
        {
            // Corrida de entregas com o mesmo id — tratar como duplicata.
            db.Entry(db.WebhookDeliveries.Local
                    .First(d => d.DeliveryId == deliveryId))
                .State = EntityState.Detached;
            return true;
        }
    }

    private static ScmMentionContext? ParseMention(
        string eventType, JsonElement root, string? installationId)
    {
        string? body = null;
        string? author = null;
        string? repository = null;
        string? branch = null;
        string? prUrl = null;
        int? prNumber = null;
        long? inReplyTo = null;

        if (root.TryGetProperty("repository", out var repo)
            && repo.TryGetProperty("full_name", out var fn))
        {
            repository = fn.GetString();
        }
        if (repository is null)
        {
            return null;
        }

        switch (eventType)
        {
            case "issue_comment":
                if (!root.TryGetProperty("comment", out var c1))
                    return null;
                body = c1.TryGetProperty("body", out var b1)
                    ? b1.GetString() : null;
                author = c1.TryGetProperty("user", out var u1)
                    && u1.TryGetProperty("login", out var l1)
                        ? l1.GetString() : null;
                if (root.TryGetProperty("issue", out var issue)
                    && issue.TryGetProperty("pull_request", out var pr1)
                    && pr1.TryGetProperty("html_url", out var u))
                {
                    prUrl = u.GetString();
                    prNumber = issue.GetProperty("number").GetInt32();
                }
                break;

            case "pull_request_review_comment":
                if (!root.TryGetProperty("comment", out var c2))
                    return null;
                body = c2.TryGetProperty("body", out var b2)
                    ? b2.GetString() : null;
                author = c2.TryGetProperty("user", out var u2)
                    && u2.TryGetProperty("login", out var l2)
                        ? l2.GetString() : null;
                if (c2.TryGetProperty("in_reply_to_id", out var r2)
                    && r2.ValueKind == JsonValueKind.Number)
                {
                    inReplyTo = r2.GetInt64();
                }
                if (root.TryGetProperty("pull_request", out var pr2))
                {
                    prUrl = pr2.TryGetProperty("html_url", out var u3)
                        ? u3.GetString() : null;
                    prNumber = pr2.TryGetProperty("number", out var n3)
                        ? n3.GetInt32() : null;
                    branch = pr2.TryGetProperty("head", out var h3)
                        && h3.TryGetProperty("ref", out var rf)
                            ? rf.GetString() : null;
                }
                break;

            case "pull_request_review":
                if (!root.TryGetProperty("review", out var rv))
                    return null;
                body = rv.TryGetProperty("body", out var b3)
                    ? b3.GetString() : null;
                author = rv.TryGetProperty("user", out var u4)
                    && u4.TryGetProperty("login", out var l4)
                        ? l4.GetString() : null;
                if (root.TryGetProperty("pull_request", out var pr3))
                {
                    prUrl = pr3.TryGetProperty("html_url", out var u5)
                        ? u5.GetString() : null;
                    prNumber = pr3.TryGetProperty("number", out var n5)
                        ? n5.GetInt32() : null;
                    branch = pr3.TryGetProperty("head", out var h5)
                        && h5.TryGetProperty("ref", out var rf5)
                            ? rf5.GetString() : null;
                }
                break;

            default:
                return null;
        }

        if (body is null || !body.Contains(Mention, StringComparison.Ordinal))
        {
            return null;
        }

        return new ScmMentionContext(
            ScmKind.GitHub, repository, branch, prUrl, prNumber,
            author, body, inReplyTo, installationId);
    }

    private async Task<Guid?> ResolveOwnerUserIdAsync(
        ScmConnection connection, CancellationToken ct)
    {
        if (connection.CreatedByUserId is not null
            && Guid.TryParse(connection.CreatedByUserId, out var createdBy))
        {
            return createdBy;
        }
        if (connection.OrgId is { } orgId)
        {
            var owner = await db.OrgMembers.IgnoreQueryFilters()
                .Where(m => m.OrgId == orgId && m.Role == "owner")
                .Select(m => m.UserId)
                .FirstOrDefaultAsync(ct);
            if (owner != Guid.Empty)
            {
                return owner;
            }
        }
        return null;
    }

    private static string BuildPrompt(
        ScmMentionContext mention, JsonElement root)
    {
        var sb = new StringBuilder();
        sb.Append("[SCM trigger: github webhook]\n");
        sb.Append($"Repository: {mention.Repository}\n");
        if (mention.PullRequestNumber is { } n)
        {
            sb.Append($"Pull request: #{n} ({mention.PullRequestUrl})\n");
        }
        if (mention.Branch is not null)
        {
            sb.Append($"Branch: {mention.Branch}\n");
        }
        sb.Append($"Mentioned by: {mention.CommentAuthor}\n");
        sb.Append("Instruction:\n");
        sb.Append(mention.CommentBody.Replace(Mention, "").Trim());
        return sb.ToString();
    }
}
