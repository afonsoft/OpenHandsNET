namespace OpenHandsNET.Application.Agent;

/// <summary>Lifecycle of one subagent run (SPEC-28 RF-003).</summary>
public enum SubagentStatus
{
    /// <summary>Still executing.</summary>
    Running,

    /// <summary>Returned a summary via <c>finish</c> or final text.</summary>
    Completed,

    /// <summary>Died with an error (exception, iteration cap).</summary>
    Failed,

    /// <summary>Cancelled by user interrupt/pause or host shutdown.</summary>
    Aborted,
}

/// <summary>Public snapshot of a subagent run (in-memory only — SPEC-28 §4).</summary>
public sealed record SubagentRunInfo(
    string TaskId,
    string SubagentType,
    string Description,
    SubagentStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt = null,
    string? Result = null,
    string? Error = null);

/// <summary>
/// Internal handle tracked by <see cref="ISubagentHost"/>: info plus the
/// cancellation/completion plumbing for background runs.
/// </summary>
public sealed class SubagentRun
{
    public required SubagentRunInfo Info { get; set; }
    public CancellationTokenSource? Cts { get; set; }

    /// <summary>Background completion (result text or error), if launched async.</summary>
    public Task<string>? Completion { get; set; }
}

/// <summary>
/// Per-conversation registry of active/finished subagent runs (SPEC-28).
/// Singleton, process-scoped — runs don't survive restarts (reported
/// <c>Aborted</c> on next status query).
/// </summary>
public interface ISubagentHost
{
    /// <summary>
    /// Registers <paramref name="run"/> when active count for the
    /// conversation is below <paramref name="maxConcurrent"/>.
    /// </summary>
    bool TryStart(Guid conversationId, SubagentRun run, int maxConcurrent);

    /// <summary>Marks the run terminal (status/result/error + EndedAt).</summary>
    void Finish(Guid conversationId, string taskId, SubagentStatus status,
        string? result = null, string? error = null);

    /// <summary>One run by id.</summary>
    SubagentRun? Get(Guid conversationId, string taskId);

    /// <summary>All runs of the conversation (newest first).</summary>
    IReadOnlyList<SubagentRunInfo> List(Guid conversationId);

    /// <summary>Cancels every running child (interrupt/pause propagation).</summary>
    void AbortAll(Guid conversationId);
}
