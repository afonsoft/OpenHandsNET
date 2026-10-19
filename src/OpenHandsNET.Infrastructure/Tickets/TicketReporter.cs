using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Tickets;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Tickets;

/// <summary>
/// Write-back no ticket ao fim da run (SPEC-55 RF-004): comentário com
/// resumo + link da conversa/PR + transição para <c>done_state</c>
/// configurado. Melhor esforço — nunca falha a run.
/// </summary>
public sealed class TicketReporter(
    AppDbContext db,
    IReadOnlyDictionary<TicketKind, ITicketProvider> providers,
    Application.Identity.IAuditService audit,
    ILogger<TicketReporter> logger) : ITicketReporter
{
    /// <inheritdoc />
    public async Task ReportRunFinishedAsync(
        Guid conversationId, bool success, string? summary,
        string? pullRequestUrl, CancellationToken ct)
    {
        try
        {
            var conv = await db.Conversations.IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    c => c.Id == conversationId, ct);
            if (conv?.TicketKind is null || conv.TicketId is null)
            {
                return;
            }
            var kind = TicketKindExtensions.Parse(conv.TicketKind);
            if (kind is null
                || !providers.TryGetValue(kind.Value, out var provider))
            {
                return;
            }
            // Conexão do mesmo kind na org da conversa.
            var all = await db.TicketConnections.IgnoreQueryFilters()
                .Where(t => t.Kind == conv.TicketKind)
                .ToListAsync(ct);
            var connection = all
                .FirstOrDefault(t => t.OrgId == conv.OrgId)
                ?? (all.Count == 1 ? all[0] : null);
            if (connection is null)
            {
                return;
            }
            var model = connection.ToModel();

            var safe = summary is null
                ? null
                : Agent.SecretRedactor.Redact(summary);
            var text = success
                ? $"OpenHandsNET: run finished — " +
                  $"{safe ?? "see conversation"}." +
                  (pullRequestUrl is null
                      ? "" : $" PR: {pullRequestUrl}") +
                  $" Conversation: {conversationId}"
                : $"OpenHandsNET: run failed — " +
                  $"conversation {conversationId}.";
            await provider.AddCommentAsync(
                model, conv.TicketId, text, ct);

            if (success)
            {
                var trigger = ParseTrigger(
                    connection.TriggerConfigJson);
                if (trigger.DoneState is not null)
                {
                    await provider.TransitionAsync(
                        model, conv.TicketId, trigger.DoneState, ct);
                }
            }

            await audit.RecordAsync("ticket.writeback",
                orgId: conv.OrgId, targetType: "app_conversation",
                targetId: conversationId.ToString(),
                metadata: new
                {
                    kind = conv.TicketKind,
                    conv.TicketId,
                    success,
                },
                cancellationToken: ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "ticket writeback failed (conv {Id})", conversationId);
        }
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
}