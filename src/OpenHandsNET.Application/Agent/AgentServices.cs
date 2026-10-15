using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Application.Agent;

/// <summary>
/// Runs the ReAct loop for one conversation (SPEC-09 RF-002). Instances are
/// created per run by <see cref="IAgentRunnerHost"/> and consume/produce events
/// — the event stream is the only source of truth.
/// </summary>
public interface IAgentRunner
{
    /// <summary>
    /// Runs the loop until finish/interrupt/pause/error/stuck. Returns when the
    /// run terminates; the host drops the runner and a later message re-runs it
    /// (resume from the last event).
    /// </summary>
    Task RunAsync(CancellationToken ct);
}

/// <summary>
/// Owns one active <see cref="IAgentRunner"/> per conversation and routes the
/// interactive signals (message kickoff, confirmation decisions, interrupt,
/// pause) to it.
/// </summary>
public interface IAgentRunnerHost
{
    /// <summary>
    /// Wakes/creates the runner for a conversation after a user message.
    /// Idempotent — a running runner picks the message up from the event stream.
    /// </summary>
    Task NotifyMessageAsync(Guid conversationId, CancellationToken ct);

    /// <summary>
    /// Delivers a confirmation-mode decision for a pending action. Always
    /// appends the audit event even when no runner is waiting.
    /// </summary>
    Task ConfirmActionAsync(
        Guid conversationId, string actionId, bool approved, string? reason,
        CancellationToken ct);

    /// <summary>Requests a graceful interrupt (finishes the in-flight tool call).</summary>
    Task InterruptAsync(Guid conversationId, CancellationToken ct);

    /// <summary>Requests a graceful pause (interrupt + status paused).</summary>
    Task PauseAsync(Guid conversationId, CancellationToken ct);

    /// <summary>Whether a runner is currently active for the conversation.</summary>
    bool IsRunning(Guid conversationId);
}

/// <summary>
/// Per-conversation task checklist behind the <c>task_tracker</c> tool —
/// updates surface as <c>ConversationStateUpdateEvent(key=plan)</c>.
/// </summary>
public interface ITaskTrackerService
{
    /// <summary>Replaces the plan and returns the emitted event.</summary>
    Task<ConversationStateUpdateEvent> SetPlanAsync(
        Guid conversationId, IReadOnlyList<TaskTrackerItem> items,
        CancellationToken ct);

    /// <summary>Current plan (empty when unset).</summary>
    IReadOnlyList<TaskTrackerItem> GetPlan(Guid conversationId);
}

/// <summary>One checklist entry.</summary>
public sealed record TaskTrackerItem(string Title, string Status, string? Notes = null);

/// <summary>
/// Context-window condensation (SPEC-09 seam, SPEC-22 real impl): evaluates
/// the history against the model window, prunes/summarizes when needed and
/// reports the plan so the runner can emit V1 condensation events.
/// </summary>
public interface ICondenser
{
    /// <summary>
    /// Threshold evaluation only — returns a <see cref="CompactionStart"/>
    /// describing the pending compaction, or null when under the threshold
    /// (or disabled). Cheap: token estimation, no LLM calls.
    /// </summary>
    Task<CompactionStart?> EvaluateAsync(
        IReadOnlyList<LlmMessage> messages, string? model, CancellationToken ct);

    /// <summary>
    /// Executes a compaction for <paramref name="trigger"/> (from
    /// <see cref="EvaluateAsync"/> or a forced trigger). When summarization
    /// starts, <paramref name="onCompacting"/> fires first so the runner can
    /// emit <c>CondensationRequest</c>.
    /// </summary>
    Task<CondenseResult> CondenseAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<string?> eventIds,
        CompactionTrigger trigger,
        string? model,
        Func<CompactionStart, Task>? onCompacting,
        CancellationToken ct);
}
