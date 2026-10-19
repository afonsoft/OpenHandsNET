namespace OpenHandsNET.Application.Agent;

/// <summary>
/// What triggered a context compaction (SPEC-22 RF-003/004) — emitted on
/// <c>CondensationRequest.Trigger</c> and in the <c>context_state</c> ledger.
/// </summary>
public enum CompactionTrigger
{
    /// <summary>Token estimate crossed the configured threshold.</summary>
    Threshold,

    /// <summary>Mid-turn check inside a running tool loop.</summary>
    MidTurn,

    /// <summary>Provider rejected the request for context overflow.</summary>
    Overflow,

    /// <summary>Completion ended with <c>finish_reason=length</c>.</summary>
    StopLength,

    /// <summary>User/API requested compaction.</summary>
    Manual,

    /// <summary>Model switch reduced the effective window.</summary>
    ModelSwitch,
}

/// <summary>Information passed to the runner just before summarization starts.</summary>
public sealed record CompactionStart(
    CompactionTrigger Trigger, int TokensBefore, int? Window);

/// <summary>
/// Outcome of <see cref="ICondenser.CondenseAsync"/>: the (possibly reduced)
/// history plus the data the runner needs to emit V1 compaction events.
/// </summary>
/// <param name="Messages">Resulting message list.</param>
/// <param name="ResultEventIds">Event ids aligned with <see cref="Messages"/> (null = synthesized).</param>
/// <param name="ForgottenEventIds">Ids of events whose messages left the context.</param>
/// <param name="PrunedEventIds">Ids of observation events whose tool output was pruned.</param>
/// <param name="Summary">LLM-produced summary (null when prune-only/passthrough).</param>
/// <param name="SummaryOffset">Index where the summary sits in <see cref="Messages"/>.</param>
/// <param name="SummarizerResponseId">LLM response id of the summary call.</param>
/// <param name="TokensBefore">Estimated tokens before compaction.</param>
/// <param name="TokensAfter">Estimated tokens after compaction.</param>
public sealed record CondenseResult(
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<string?> ResultEventIds,
    IReadOnlyList<string> ForgottenEventIds,
    IReadOnlyList<string> PrunedEventIds,
    string? Summary,
    int? SummaryOffset,
    string? SummarizerResponseId,
    int TokensBefore,
    int TokensAfter)
{
    /// <summary>Whether a compaction actually happened.</summary>
    public bool Compacted =>
        Summary is not null || ForgottenEventIds.Count > 0
        || PrunedEventIds.Count > 0;
}

/// <summary>
/// Token estimation + context-window knowledge per model (SPEC-22 RF-001).
/// Scoped per run — <see cref="ObserveUsage"/> calibrates estimates online.
/// </summary>
public interface IContextMeter
{
    /// <summary>Estimated prompt tokens for <paramref name="messages"/>.</summary>
    int EstimateTokens(IReadOnlyList<LlmMessage> messages, string? model);

    /// <summary>Effective context window for <paramref name="model"/> (null = unknown).</summary>
    int? ContextWindow(string? model);

    /// <summary>Feeds back an observed prompt_tokens so later estimates improve.</summary>
    void ObserveUsage(int promptTokens, int estimatedTokens);
}

/// <summary>
/// Provider rejected the request because the context exceeded the model
/// window (SPEC-22 RF-004). Distinct from generic errors — triggers
/// promote/compact + one retry instead of a fatal run error.
/// </summary>
public sealed class ContextOverflowException : LlmCallException
{
    /// <summary>Creates the exception with a redacted detail message.</summary>
    public ContextOverflowException(string message)
        : base(LlmErrorKind.ContextOverflow, message, 400)
    {
    }
}

/// <summary>
/// Compaction outside the running loop (SPEC-22 RF-004 manual trigger):
/// rebuilds the history from the event stream, compacts, emits the events.
/// </summary>
public interface ICompactionService
{
    /// <summary>
    /// Compacts the stored event-derived history. Returns
    /// <see langword="true"/> when a condensation was emitted.
    /// </summary>
    Task<bool> CompactStoredAsync(
        Guid conversationId, CompactionTrigger trigger, string? instructions,
        CancellationToken ct);
}