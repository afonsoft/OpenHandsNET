using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Events;
using Sandcastle.Domain.Events;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Agent.Context;

/// <summary>
/// Compaction fora do loop (SPEC-22 RF-004 manual): reconstrói o histórico
/// dos eventos, roda o condenser e emite os V1 events — o próximo run
/// resume já com contexto compactado.
/// </summary>
public sealed class CompactionService(
    AppDbContext db,
    IEventService events,
    ConversationHistoryBuilder historyBuilder,
    ICondenser condenser,
    IContextMeter meter,
    Microsoft.Extensions.Options.IOptions<AgentLlmOptions> options,
    IServiceProvider services) : ICompactionService
{
    /// <inheritdoc />
    public async Task<bool> CompactStoredAsync(
        Guid conversationId, CompactionTrigger trigger, string? instructions,
        CancellationToken ct)
    {
        var conv = await db.Conversations.FindAsync([conversationId], ct);
        if (conv is null)
        {
            return false;
        }

        var tools = services.GetRequiredService<IToolRegistry>();
        var rebuilt = await historyBuilder.BuildAsync(
            conversationId, conv, tools, ct);
        var result = await condenser.CondenseAsync(
            rebuilt.Messages, rebuilt.EventIds, trigger,
            conv.LlmModel ?? options.Value.Model,
            onCompacting: start => EmitRequestAsync(
                conversationId, start, instructions),
            ct);
        if (!result.Compacted)
        {
            return false;
        }

        await EmitCondensationAsync(events, conversationId, result, ct);
        await EmitLedgerAsync(events, conversationId, result.TokensAfter,
            new LedgerExtras(
                meter.ContextWindow(conv.LlmModel ?? options.Value.Model),
                conv.LlmModel ?? options.Value.Model), ct);
        return true;
    }

    /// <summary>Emits the request event (shared with the runner path).</summary>
    public static Task EmitRequestAsync(
        IEventService events, Guid conversationId, CompactionStart start,
        string? instructions, CancellationToken ct = default) =>
        events.AppendAsync(conversationId, new CondensationRequest
        {
            Trigger = TriggerName(start.Trigger),
            Instructions = instructions,
            TokensBefore = start.TokensBefore,
        }, ct).AsTask();

    /// <summary>Emits condensation + summary events; returns the record.</summary>
    public static async Task<Condensation> EmitCondensationAsync(
        IEventService events, Guid conversationId, CondenseResult result,
        CancellationToken ct)
    {
        var condensation = (Condensation)await events.AppendAsync(
            conversationId, new Condensation
            {
                ForgottenEventIds = result.ForgottenEventIds,
                PrunedEventIds = result.PrunedEventIds,
                Summary = result.Summary,
                SummaryOffset = result.SummaryOffset,
                LlmResponseId = result.SummarizerResponseId ?? "condenser",
            }, ct);
        if (result.Summary is { } summary)
        {
            await events.AppendAsync(conversationId,
                new CondensationSummaryEvent
                {
                    ParentId = condensation.Id,
                    Summary = summary,
                }, ct);
        }

        return condensation;
    }

    /// <summary>Emits the <c>context_state</c> ledger state update.</summary>
    public static Task EmitLedgerAsync(
        IEventService events, Guid conversationId, int tokensEstimate,
        LedgerExtras extras, CancellationToken ct) =>
        events.AppendAsync(conversationId, new ConversationStateUpdateEvent
        {
            Key = "context_state",
            Value = JsonSerializer.SerializeToElement(new
            {
                tokens_estimate = tokensEstimate,
                extras.Window,
                extras.Compactions,
                boundary_event_id = extras.BoundaryEventId,
                extras.Model,
                updated_at = DateTimeOffset.UtcNow,
            }, EventJson.Options),
        }, ct).AsTask();

    /// <summary>Campos variáveis do ledger de contexto (SPEC-104).</summary>
    public sealed record LedgerExtras(
        int? Window, string? Model,
        int Compactions = 0, string? BoundaryEventId = null);

    /// <summary>Snake_case trigger name for the event payload.</summary>
    public static string TriggerName(CompactionTrigger t) => t switch
    {
        CompactionTrigger.MidTurn => "mid_turn",
        CompactionTrigger.StopLength => "stop_length",
        CompactionTrigger.ModelSwitch => "model_switch",
        var other => other.ToString().ToLowerInvariant(),
    };

    private Task EmitRequestAsync(
        Guid conversationId, CompactionStart start, string? instructions) =>
        EmitRequestAsync(events, conversationId, start, instructions);
}