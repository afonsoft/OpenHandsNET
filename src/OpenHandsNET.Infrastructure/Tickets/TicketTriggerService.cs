using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Application.Tickets;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Tickets;

/// <summary>
/// Pipeline de webhook de ticket (SPEC-55 RF-002/003):
/// connection (kind+workspace) → verify → parse → dedup →
/// trigger eval (label/assignee/state ou @menção em comentário) →
/// start-task com prompt enriquecido + ticket binding na conversa.
/// Conteúdo externo é contexto não-confiável — nunca executado.
/// </summary>
public sealed class TicketTriggerService(
    AppDbContext db,
    ITicketConnectionService connections,
    IReadOnlyDictionary<TicketKind, ITicketProvider> providers,
    OpenHandsNET.Application.Conversations.IAppConversationService
        conversations,
    IAuditService audit,
    ILogger<TicketTriggerService> logger) : ITicketTriggerService
{
    private const string Mention = "@openhandsnet";

    /// <inheritdoc />
    public async Task<TicketOpsResult> HandleAsync(
        TicketKind kind, string? signatureOrSecret, string? workspaceHint,
        byte[] payload, CancellationToken ct)
    {
        var connection = await connections.FindForWebhookAsync(
            kind, workspaceHint, ct);
        if (connection is null)
        {
            return TicketOpsResult.NoConnection;
        }
        if (!providers.TryGetValue(kind, out var provider))
        {
            return TicketOpsResult.NoConnection;
        }
        if (!await provider.VerifyAsync(
                connection, signatureOrSecret, payload, ct))
        {
            return TicketOpsResult.InvalidSignature;
        }
        var ev = provider.ParseWebhook(payload);
        if (ev is null)
        {
            return TicketOpsResult.BadPayload;
        }
        if (await IsDuplicateAsync(ev.DeliveryId, ct))
        {
            return TicketOpsResult.Duplicate;
        }

        var trigger = ParseTrigger(connection.TriggerConfigJson);
        var hit = Evaluate(ev, trigger);
        if (!hit)
        {
            return TicketOpsResult.Ignored;
        }
        await StartAsync(connection, trigger, ev, ct);
        return TicketOpsResult.Processed;
    }

    /// <inheritdoc />
    public async Task<TicketOpsResult> RunAsync(
        Guid connectionId, string issueKey, CancellationToken ct)
    {
        var connection = await connections.GetAsync(connectionId, ct);
        if (connection is null
            || !providers.TryGetValue(connection.Kind, out var provider))
        {
            return TicketOpsResult.NoConnection;
        }
        var issue = await provider.GetIssueAsync(
            connection, issueKey, ct);
        if (issue is null)
        {
            return TicketOpsResult.BadPayload;
        }
        var trigger = ParseTrigger(connection.TriggerConfigJson);
        var ev = new TicketWebhookEvent(
            "manual_run", issue.Key, null,
            null, null, null, null, issue.ProjectKey,
            issue.Title, issue.Description, issue.Url,
            issue.Labels, issue.State, issue.Assignee);
        await StartAsync(connection, trigger, ev, ct);
        return TicketOpsResult.Processed;
    }

    private static TicketTriggerConfig ParseTrigger(string? json)
    {
        if (json is null)
        {
            return new TicketTriggerConfig();
        }
        try
        {
            return JsonSerializer.Deserialize<TicketTriggerConfig>(json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        PropertyNamingPolicy =
                            JsonNamingPolicy.SnakeCaseLower,
                    })
                ?? new TicketTriggerConfig();
        }
        catch (JsonException)
        {
            return new TicketTriggerConfig();
        }
    }

    /// <summary>
    /// Trigger (SPEC-55 RF-002): comentário com @openhandsnet dispara
    /// sempre; issue_updated dispara quando o campo mudado casa o
    /// <see cref="TicketTriggerConfig.Mode"/>+Value.
    /// </summary>
    private static bool Evaluate(
        TicketWebhookEvent ev, TicketTriggerConfig cfg)
    {
        if (ev.EventKind == "manual_run")
        {
            return true;
        }
        if (ev.EventKind == "comment_created")
        {
            return ev.CommentBody?.Contains(Mention,
                StringComparison.OrdinalIgnoreCase) == true;
        }
        var match = cfg.Mode switch
        {
            "label" => string.Equals(ev.AddedLabel, cfg.Value,
                StringComparison.OrdinalIgnoreCase)
                || ev.Labels?.Contains(cfg.Value,
                    StringComparer.OrdinalIgnoreCase) == true
                    && ev.AddedLabel is not null,
            "assignee" => string.Equals(ev.NewAssignee, cfg.Value,
                StringComparison.OrdinalIgnoreCase),
            "state" => string.Equals(ev.NewState, cfg.Value,
                StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        return match;
    }

    private async Task StartAsync(
        TicketConnection c, TicketTriggerConfig trigger,
        TicketWebhookEvent ev, CancellationToken ct)
    {
        // Projeto → repo/branch sobrepõe o default da connection.
        TicketProjectConfig? proj = ev.ProjectKey is null
            ? null
            : await connections.GetProjectConfigAsync(
                c.Id, ev.ProjectKey, ct);
        var repo = proj?.SelectedRepository ?? trigger.SelectedRepository;
        var branch = proj?.SelectedBranch;

        var prompt = RenderPrompt(trigger.PromptTemplate, ev, repo, branch);
        var ownerUserId = await ResolveOwnerAsync(c, ct);
        if (ownerUserId is null)
        {
            logger.LogWarning(
                "tickets: nenhum owner na org {Org} p/ conversa de {Key}",
                c.OrgId, ev.IssueKey);
            return;
        }

        var task = await conversations.CreateForUserAsync(
            ownerUserId.Value, c.OrgId,
            new CreateConversationRequest(
                new InitialMessage("user", [prompt], Run: true),
                repo,
                AgentType: null, LlmModel: null, SandboxSpecId: null),
            ct);

        var convId = task.AppConversationId;
        if (convId is not null)
        {
            var rec = await db.Conversations.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == convId.Value, ct);
            if (rec is not null)
            {
                rec.TicketKind = c.Kind.ToWire();
                rec.TicketId = ev.IssueKey;
                rec.TicketUrl = ev.Url;
                await db.SaveChangesAsync(ct);
            }
        }

        await audit.RecordAsync("ticket.run_started",
            actorUserId: ownerUserId.Value.ToString(), orgId: c.OrgId,
            targetType: "app_conversation",
            targetId: convId?.ToString(),
            metadata: new
            {
                kind = c.Kind.ToWire(), ev.IssueKey, ev.EventKind,
            },
            cancellationToken: ct);
    }

    private async Task<Guid?> ResolveOwnerAsync(
        TicketConnection c, CancellationToken ct)
    {
        if (c.OrgId is null)
        {
            return null;
        }
        return await db.OrgMembers.IgnoreQueryFilters()
            .Where(m => m.OrgId == c.OrgId && m.Role == "owner")
            .Select(m => (Guid?)m.UserId)
            .FirstOrDefaultAsync(ct)
            ?? await db.OrgMembers.IgnoreQueryFilters()
                .Where(m => m.OrgId == c.OrgId)
                .Select(m => (Guid?)m.UserId)
                .FirstOrDefaultAsync(ct);
    }

    internal static string RenderPrompt(
        string? template, TicketWebhookEvent ev,
        string? repo, string? branch)
    {
        var labels = ev.Labels is null
            ? "" : string.Join(", ", ev.Labels);
        if (template is not null)
        {
            return template
                .Replace("{identifier}", ev.IssueKey)
                .Replace("{title}", ev.Title ?? "")
                .Replace("{description}", ev.Description ?? "")
                .Replace("{labels}", labels)
                .Replace("{url}", ev.Url ?? "")
                .Replace("{comment}", ev.CommentBody ?? "");
        }
        var sb = new System.Text.StringBuilder();
        sb.Append("Ticket ").Append(ev.IssueKey);
        if (ev.Title is not null)
        {
            sb.Append(": ").Append(ev.Title);
        }
        sb.Append('\n');
        if (ev.Description is not null)
        {
            sb.Append('\n').Append(ev.Description).Append('\n');
        }
        if (labels.Length > 0)
        {
            sb.Append("\nLabels: ").Append(labels);
        }
        if (ev.CommentBody is not null)
        {
            sb.Append("\n\nTrigger comment: ").Append(ev.CommentBody);
        }
        if (repo is not null)
        {
            sb.Append("\n\nRepository: ").Append(repo);
            if (branch is not null)
            {
                sb.Append(" (branch: ").Append(branch).Append(')');
            }
        }
        if (ev.Url is not null)
        {
            sb.Append("\nTicket: ").Append(ev.Url);
        }
        return sb.ToString();
    }

    private async Task<bool> IsDuplicateAsync(
        string? deliveryId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(deliveryId))
        {
            return false;
        }
        var exists = await db.WebhookDeliveries.IgnoreQueryFilters()
            .AnyAsync(d => d.Kind == "ticket"
                && d.DeliveryId == deliveryId, ct);
        if (exists)
        {
            return true;
        }
        db.WebhookDeliveries.Add(
            new OpenHandsNET.Infrastructure.Scm.WebhookDeliveryRecord
            {
                Id = Guid.NewGuid(), Kind = "ticket",
                DeliveryId = deliveryId,
                ProcessedAt = DateTimeOffset.UtcNow,
            });
        try
        {
            await db.SaveChangesAsync(ct);
            return false;
        }
        catch (DbUpdateException)
        {
            return true;
        }
    }
}
