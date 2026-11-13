using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Alerts;
using Sandcastle.Application.ChatOps;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Identity;
using Sandcastle.Infrastructure.ChatOps;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Alerts;

/// <summary>
/// SPEC-151 RF-002 — pipeline alerta→sessão, compartilhado entre o
/// path automático (webhook) e o manual
/// (<c>POST /alerts/events/{id}/create-session</c>): mapping → dedup
/// <c>(source, external_id)</c> → cap <c>Alerts:MaxSessionsPerHour</c>
/// → conversa <c>agent_type: incident-investigator</c>. Notificação
/// chat best-effort quando a org tem connection com canal default.
/// </summary>
public sealed class AlertSessionPipeline(
    AppDbContext db,
    IAppConversationService conversations,
    IChatSink chatSink,
    IAuditService audit,
    IOptions<AlertOptions> options,
    ILogger<AlertSessionPipeline> logger)
{
    /// <summary>
    /// Tenta criar a sessão de investigação para o evento.
    /// <paramref name="applySeverityGate"/> = path automático
    /// (severity ≥ triage_severity_min); o manual explícito pula o gate.
    /// </summary>
    public async Task<AlertSessionResult> TryCreateAsync(
        AlertSourceRecord source, AlertEventRecord evt,
        Guid? actorUserId, bool applySeverityGate, CancellationToken ct)
    {
        if (evt.Repository is null)
        {
            return AlertSessionResult.Fail(AlertSessionError.Unrouted);
        }
        if (evt.SessionId is not null)
        {
            return AlertSessionResult.Fail(AlertSessionError.Duplicate);
        }
        if (applySeverityGate
            && AlertSeverities.Rank(evt.Severity)
                < AlertSeverities.Rank(source.TriageSeverityMin))
        {
            return AlertSessionResult.Fail(AlertSessionError.BelowSeverity);
        }

        // Cap por source/hora — conta sessões já criadas na janela.
        var cap = options.Value.MaxSessionsPerHour;
        if (cap > 0)
        {
            // SQLite não ordena DateTimeOffset — compara o espelho
            // *Ticks (pattern do DbContext, SPEC-10).
            var cutoffTicks = DateTimeOffset.UtcNow
                .AddHours(-1).UtcTicks;
            var recent = await db.AlertEvents.IgnoreQueryFilters()
                .CountAsync(e => e.SourceId == source.Id
                    && e.SessionId != null
                    && e.CreatedAtTicks >= cutoffTicks, ct);
            if (recent >= cap)
            {
                await audit.RecordAsync("alert.session_quota_exceeded",
                    orgId: source.OrgId, targetType: "alert_event",
                    targetId: evt.Id.ToString(),
                    metadata: new { cap, recent }, cancellationToken: ct);
                return AlertSessionResult.Fail(
                    AlertSessionError.QuotaExceeded);
            }
        }

        var owner = actorUserId ?? await ResolveOwnerAsync(source, ct);
        if (owner is null)
        {
            logger.LogWarning(
                "alert session without resolvable owner (source {Id})",
                source.Id);
            return AlertSessionResult.Fail(AlertSessionError.NotFound);
        }

        var prompt = AlertPromptBuilder.BuildInvestigationPrompt(
            source.Provider, source.Name, evt.Title, evt.Severity,
            evt.Repository, evt.Url, evt.Service, evt.Culprit,
            evt.LabelsJson);
        var task = await conversations.CreateForUserAsync(
            owner.Value, source.OrgId,
            new CreateConversationRequest(
                new InitialMessage("user", [prompt], Run: true),
                evt.Repository,
                AgentType: "incident-investigator",
                LlmModel: null, SandboxSpecId: null),
            ct);

        evt.SessionId = task.AppConversationId;
        evt.Status = AlertEventStatuses.Investigating;
        evt.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("alert.session_created",
            actorUserId: owner.Value.ToString(), orgId: source.OrgId,
            targetType: "alert_event", targetId: evt.Id.ToString(),
            metadata: new
            {
                provider = source.Provider,
                repository = evt.Repository,
                session_id = task.AppConversationId?.ToString(),
            },
            cancellationToken: ct);

        await NotifyChatAsync(source, evt, ct);
        return AlertSessionResult.Ok(
            task.AppConversationId ?? Guid.Empty);
    }

    /// <summary>
    /// Notificação best-effort: posta no canal default da primeira
    /// chat connection da org (Slack ou Teams via IChatSink router).
    /// Falhas só logam — nunca derrubam o pipeline.
    /// </summary>
    private async Task NotifyChatAsync(
        AlertSourceRecord source, AlertEventRecord evt, CancellationToken ct)
    {
        try
        {
            if (source.OrgId is not { } orgId)
            {
                return;
            }
            var conn = await db.ChatConnections.IgnoreQueryFilters()
                .Where(c => c.OrgId == orgId)
                .OrderBy(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (conn is null)
            {
                return;
            }

            var model = ChatConnectionService.ToModel(conn);
            var text = $"🚨 Alert {evt.Title} → investigation session "
                + $"started ({evt.Repository}).";
            await chatSink.PostMessageAsync(
                model, model.DefaultChannelId ?? "general", null,
                text, ct);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "alert chat notify failed (event {Id})",
                evt.Id);
        }
    }

    private async Task<Guid?> ResolveOwnerAsync(
        AlertSourceRecord source, CancellationToken ct)
    {
        if (source.CreatedByUserId is not null
            && Guid.TryParse(source.CreatedByUserId, out var createdBy))
        {
            return createdBy;
        }
        if (source.OrgId is { } orgId)
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
}