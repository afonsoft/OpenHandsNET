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
using OpenHandsNET.Infrastructure.Security;

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
    private const string Repository = "repository";
    private const string Comment = "comment";
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
            var repository = root.TryGetProperty(Repository, out var repo)
                && repo.TryGetProperty("full_name", out var fn)
                    ? fn.GetString() : null;

            var connection = await FindConnectionAsync(
                ScmKind.GitHub, installationId, repository, ct);
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

            if (await IsDuplicateAsync(
                    ScmKind.GitHub, deliveryId, payload, ct))
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

            var prompt = BuildPrompt(mention);
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
        ScmKind kind, string? installationId, string? repository,
        CancellationToken ct)
    {
        var query = db.ScmConnections.IgnoreQueryFilters()
            .Where(c => c.Kind == kind.ToWire());
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
        ScmKind kind, string? deliveryId, byte[] payload, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(deliveryId))
        {
            return false;
        }

        var wire = kind.ToWire();
        var exists = await db.WebhookDeliveries.IgnoreQueryFilters()
            .AnyAsync(d => d.Kind == wire && d.DeliveryId == deliveryId, ct);
        if (exists)
        {
            return true;
        }

        db.WebhookDeliveries.Add(new WebhookDeliveryRecord
        {
            Id = Guid.NewGuid(),
            Kind = wire,
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

    private sealed record MentionFields(
        string? Body, string? Author, string? Branch,
        string? PrUrl, int? PrNumber, long? InReplyTo);

    private static ScmMentionContext? ParseMention(
        string eventType, JsonElement root, string? installationId)
    {
        string? repository = null;
        if (root.TryGetProperty(Repository, out var repo)
            && repo.TryGetProperty("full_name", out var fn))
        {
            repository = fn.GetString();
        }
        if (repository is null)
        {
            return null;
        }

        var f = eventType switch
        {
            "issue_comment" => ParseIssueComment(root),
            "pull_request_review_comment" => ParsePrReviewComment(root),
            "pull_request_review" => ParsePrReview(root),
            _ => null,
        };
        if (f?.Body is null || !f.Body.Contains(Mention, StringComparison.Ordinal))
        {
            return null;
        }

        return new ScmMentionContext(
            ScmKind.GitHub, repository, f.Branch, f.PrUrl, f.PrNumber,
            f.Author, f.Body, f.InReplyTo, installationId);
    }

    private static (string? PrUrl, int? PrNumber, string? Branch) PullRefOf(JsonElement pr)
        => (pr.TryGetProperty("html_url", out var u) ? u.GetString() : null,
            pr.TryGetProperty("number", out var n) ? n.GetInt32() : null,
            pr.TryGetProperty("head", out var h)
                && h.TryGetProperty("ref", out var rf)
                    ? rf.GetString() : null);

    private static MentionFields? ParseIssueComment(JsonElement root)
    {
        if (!root.TryGetProperty(Comment, out var c))
        {
            return null;
        }
        var body = c.TryGetProperty("body", out var b) ? b.GetString() : null;
        var author = c.TryGetProperty("user", out var u)
            && u.TryGetProperty("login", out var l)
                ? l.GetString() : null;
        string? prUrl = null;
        int? prNumber = null;
        if (root.TryGetProperty("issue", out var issue)
            && issue.TryGetProperty("pull_request", out var pr)
            && pr.TryGetProperty("html_url", out var pu))
        {
            prUrl = pu.GetString();
            prNumber = issue.GetProperty("number").GetInt32();
        }
        return new MentionFields(body, author, null, prUrl, prNumber, null);
    }

    private static MentionFields? ParsePrReviewComment(JsonElement root)
    {
        if (!root.TryGetProperty(Comment, out var c))
        {
            return null;
        }
        var body = c.TryGetProperty("body", out var b) ? b.GetString() : null;
        var author = c.TryGetProperty("user", out var u)
            && u.TryGetProperty("login", out var l)
                ? l.GetString() : null;
        long? inReplyTo = null;
        if (c.TryGetProperty("in_reply_to_id", out var r)
            && r.ValueKind == JsonValueKind.Number)
        {
            inReplyTo = r.GetInt64();
        }
        string? prUrl = null, branch = null;
        int? prNumber = null;
        if (root.TryGetProperty("pull_request", out var pr))
        {
            (prUrl, prNumber, branch) = PullRefOf(pr);
        }
        return new MentionFields(body, author, branch, prUrl, prNumber, inReplyTo);
    }

    private static MentionFields? ParsePrReview(JsonElement root)
    {
        if (!root.TryGetProperty("review", out var rv))
        {
            return null;
        }
        var body = rv.TryGetProperty("body", out var b) ? b.GetString() : null;
        var author = rv.TryGetProperty("user", out var u)
            && u.TryGetProperty("login", out var l)
                ? l.GetString() : null;
        string? prUrl = null, branch = null;
        int? prNumber = null;
        if (root.TryGetProperty("pull_request", out var pr))
        {
            (prUrl, prNumber, branch) = PullRefOf(pr);
        }
        return new MentionFields(body, author, branch, prUrl, prNumber, null);
    }

    /// <inheritdoc />
    public async Task<ScmWebhookResult> HandleAsync(
        ScmKind kind, string? signatureOrToken, string? eventType,
        string? deliveryId, byte[] payload, CancellationToken ct)
    {
        if (kind == ScmKind.GitHub)
        {
            return await HandleGitHubAsync(
                signatureOrToken, eventType ?? "", deliveryId, payload, ct);
        }

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
            var repository = ExtractRepository(kind, root);
            var connection = await FindConnectionAsync(
                kind, null, repository, ct);
            if (connection is null)
            {
                return ScmWebhookResult.NoConnection;
            }

            if (!await VerifyByKindAsync(
                    kind, connection, signatureOrToken, payload, ct))
            {
                await audit.RecordAsync("scm.webhook_rejected", orgId: null,
                    targetType: "scm_connection",
                    targetId: connection.Id.ToString(),
                    metadata: new
                    {
                        reason = "bad_signature",
                        eventType,
                        kind = kind.ToWire(),
                    },
                    cancellationToken: ct);
                return ScmWebhookResult.InvalidSignature;
            }

            if (await IsDuplicateAsync(kind, deliveryId, payload, ct))
            {
                return ScmWebhookResult.Duplicate;
            }

            var mention = ParseMentionByKind(kind, eventType, root);
            if (mention is null)
            {
                return ScmWebhookResult.Ignored;
            }

            var ownerUserId = await ResolveOwnerUserIdAsync(connection, ct);
            if (ownerUserId is null)
            {
                return ScmWebhookResult.Ignored;
            }

            var prompt = BuildPrompt(mention);
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
                    kind = kind.ToWire(),
                    repository = mention.Repository,
                    pr = mention.PullRequestNumber,
                },
                cancellationToken: ct);
            return ScmWebhookResult.Processed;
        }
    }

    /// <summary>SPEC-53: verificação por kind (token | HMAC | shared secret).</summary>
    private async Task<bool> VerifyByKindAsync(
        ScmKind kind, ScmConnection connection, string? signatureOrToken,
        byte[] payload, CancellationToken ct)
    {
        if (kind == ScmKind.Bitbucket)
        {
            // X-Hub-Signature "sha256=<hmac>" — mesmo esquema do GitHub.
            return await VerifySignatureAsync(
                connection, signatureOrToken, payload, ct);
        }

        if (connection.WebhookSecretRef is null || signatureOrToken is null)
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
        // GitLab X-Gitlab-Token / AzDO shared secret: igualdade em tempo fixo.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(signatureOrToken));
    }

    /// <summary>SPEC-53: extrai "owner/repo" do payload por provider.</summary>
    private static string? ExtractRepository(ScmKind kind, JsonElement root)
    {
        switch (kind)
        {
            case ScmKind.GitLab:
                return root.TryGetProperty("project", out var p)
                    && p.TryGetProperty("path_with_namespace", out var pn)
                        ? pn.GetString() : null;
            case ScmKind.Bitbucket:
                return root.TryGetProperty(Repository, out var r)
                    && r.TryGetProperty("full_name", out var fn)
                        ? fn.GetString() : null;
            case ScmKind.AzureDevOps:
                return root.TryGetProperty("resource", out var res)
                    && res.TryGetProperty(Repository, out var rr)
                    && rr.TryGetProperty("name", out var rn)
                        ? rn.GetString() : null;
            default:
                return null;
        }
    }

    /// <summary>SPEC-53: parse da menção por provider (note/comment events).</summary>
    private static ScmMentionContext? ParseMentionByKind(
        ScmKind kind, string? eventType, JsonElement root)
    {
        var repository = ExtractRepository(kind, root);
        if (repository is null)
        {
            return null;
        }

        var f = kind switch
        {
            ScmKind.GitLab => ParseGitLabMention(eventType, root),
            ScmKind.Bitbucket => ParseBitbucketMention(eventType, root),
            ScmKind.AzureDevOps => ParseAzureMention(root),
            _ => null,
        };
        if (f?.Body is null || !f.Body.Contains(Mention, StringComparison.Ordinal))
        {
            return null;
        }
        return new ScmMentionContext(
            kind, repository, f.Branch, f.PrUrl, f.PrNumber,
            f.Author, f.Body, null, null);
    }

    private static MentionFields? ParseGitLabMention(
        string? eventType, JsonElement root)
    {
        if (eventType != "Note Hook"
            || !root.TryGetProperty("object_attributes", out var oa))
        {
            return null;
        }
        var body = oa.TryGetProperty("note", out var n)
            ? n.GetString() : null;
        var author = root.TryGetProperty("user", out var gu)
            && gu.TryGetProperty("username", out var un)
                ? un.GetString() : null;
        string? prUrl = null, branch = null;
        int? prNumber = null;
        if (oa.TryGetProperty("noteable_type", out var nt)
            && nt.GetString() == "MergeRequest"
            && root.TryGetProperty("merge_request", out var glmr))
        {
            prNumber = glmr.TryGetProperty("iid", out var ii)
                ? ii.GetInt32() : null;
            prUrl = glmr.TryGetProperty("url", out var gu2)
                ? gu2.GetString() : null;
            branch = glmr.TryGetProperty("source_branch", out var sb)
                ? sb.GetString() : null;
        }
        return new MentionFields(body, author, branch, prUrl, prNumber, null);
    }

    private static MentionFields? ParseBitbucketMention(
        string? eventType, JsonElement root)
    {
        if (eventType != "pullrequest:comment_created"
            && eventType != "issue:comment_created")
        {
            return null;
        }
        string? body = null;
        if (root.TryGetProperty(Comment, out var bc)
            && bc.TryGetProperty("content", out var bco))
        {
            body = bco.TryGetProperty("raw", out var raw)
                ? raw.GetString() : null;
        }
        var author = root.TryGetProperty(Comment, out var bcc)
            && bcc.TryGetProperty("user", out var bu)
            && bu.TryGetProperty("nickname", out var bn)
                ? bn.GetString() : null;
        string? prUrl = null;
        int? prNumber = null;
        if (root.TryGetProperty("pullrequest", out var bbpr))
        {
            prNumber = bbpr.TryGetProperty("id", out var bid)
                ? bid.GetInt32() : null;
            prUrl = bbpr.TryGetProperty("links", out var bl)
                && bl.TryGetProperty("html", out var bh)
                && bh.TryGetProperty("href", out var bh2)
                    ? bh2.GetString() : null;
        }
        return new MentionFields(body, author, null, prUrl, prNumber, null);
    }

    private static MentionFields? ParseAzureMention(JsonElement root)
    {
        if (!root.TryGetProperty("resource", out var res))
        {
            return null;
        }
        // ms.vss-code.git-pullrequest-comment-event
        if (!res.TryGetProperty(Comment, out var ac))
        {
            return null;
        }
        var body = ac.TryGetProperty("content", out var aco)
            ? aco.GetString() : null;
        var author = ac.TryGetProperty("author", out var aa)
            && aa.TryGetProperty("displayName", out var an)
                ? an.GetString() : null;
        int? prNumber = res.TryGetProperty("pullRequest", out var apr)
            && apr.TryGetProperty("pullRequestId", out var aid)
                ? aid.GetInt32() : null;
        return new MentionFields(body, author, null, null, prNumber, null);
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

    // SPEC-91: qualquer texto derivado do payload entra envelopado; o
    // cabeçalho estrutural e ids validados (nº do PR como int) ficam fora.
    internal static string BuildPrompt(
        ScmMentionContext mention)
    {
        var src = $"github:{mention.Repository}"
            + (mention.PullRequestNumber is { } pr ? $"#{pr}" : "");
        var env = new UntrustedInput.Envelope();
        var sb = new StringBuilder();
        sb.Append("[SCM trigger: github webhook]\n");
        sb.Append($"Repository: {env.Wrap(mention.Repository, src, "repository")}\n");
        if (mention.PullRequestNumber is { } n)
        {
            sb.Append($"Pull request: #{n} ({env.Wrap(mention.PullRequestUrl, src, "pull_request.url")})\n");
        }
        if (mention.Branch is not null)
        {
            sb.Append($"Branch: {env.Wrap(mention.Branch, src, "branch")}\n");
        }
        sb.Append($"Mentioned by: {env.Wrap(mention.CommentAuthor, src, "comment.author")}\n");
        sb.Append("Instruction:\n");
        sb.Append(env.Wrap(mention.CommentBody.Replace(Mention, "").Trim(),
            src, "comment.body"));
        return sb.ToString();
    }
}