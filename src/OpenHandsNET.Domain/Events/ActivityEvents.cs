using System.Text.Json.Serialization;

namespace OpenHandsNET.Domain.Events;

/// <summary>
/// Live command output chunk (kind <c>command_output</c>, SPEC-24 RF-003).
/// Host-debounced (250ms/32KB coalescing, lossless concatenation).
/// </summary>
public sealed record CommandOutputEvent : EventBase
{
    /// <summary>Creates an agent-sourced output chunk.</summary>
    public CommandOutputEvent() => Source = EventSource.Agent;

    /// <summary>Tool call this output belongs to.</summary>
    public string ToolCallId { get; init; } = "";

    /// <summary>ActionEvent id owning the tool call.</summary>
    public string? ActionId { get; init; }

    /// <summary><c>stdout</c> | <c>stderr</c>.</summary>
    public string Channel { get; init; } = "stdout";

    /// <summary>Output text (redacted before persistence).</summary>
    public string Text { get; init; } = "";
}

/// <summary>
/// Long-running tool progress line (kind <c>tool_progress</c>,
/// SPEC-24 RF-003).
/// </summary>
public sealed record ToolProgressEvent : EventBase
{
    /// <summary>Creates an agent-sourced progress update.</summary>
    public ToolProgressEvent() => Source = EventSource.Agent;

    /// <summary>Tool call this progress belongs to.</summary>
    public string ToolCallId { get; init; } = "";

    /// <summary>ActionEvent id owning the tool call.</summary>
    public string? ActionId { get; init; }

    /// <summary>Optional phase label (e.g. <c>navigating</c>, <c>running</c>).</summary>
    public string? Phase { get; init; }

    /// <summary>Human-readable progress line.</summary>
    public string Message { get; init; } = "";

    /// <summary>0–100 when the tool reports completion percentage.</summary>
    public int? Percent { get; init; }
}

/// <summary>
/// Workspace paths touched by a tool call (kind <c>files_changed</c>,
/// SPEC-24 RF-003).
/// </summary>
public sealed record FilesChangedEvent : EventBase
{
    /// <summary>Creates an agent-sourced file-change marker.</summary>
    public FilesChangedEvent() => Source = EventSource.Agent;

    /// <summary>Tool call that changed the files (nullable for bash diff).</summary>
    public string? ToolCallId { get; init; }

    /// <summary>ActionEvent id owning the tool call.</summary>
    public string? ActionId { get; init; }

    /// <summary>Tool that produced the change (file_write, file_edit, bash…).</summary>
    public string SourceTool { get; init; } = "";

    /// <summary>Changed paths with their operation.</summary>
    public IReadOnlyList<FileChange> Paths { get; init; } = [];
}

/// <summary>One changed path in a <see cref="FilesChangedEvent"/>.</summary>
public sealed record FileChange(
    string Path, string Op /* create|modify|delete */);

/// <summary>
/// Per-iteration run progress (kind <c>run_progress</c>, SPEC-24 RF-003).
/// The UI replaces — not appends — the previous one.
/// </summary>
public sealed record RunProgressEvent : EventBase
{
    /// <summary>Creates an agent-sourced progress tick.</summary>
    public RunProgressEvent() => Source = EventSource.Agent;

    /// <summary>Current ReAct iteration.</summary>
    public int Iteration { get; init; }

    /// <summary>Configured iteration bound.</summary>
    public int MaxIterations { get; init; }

    /// <summary>
    /// <c>thinking</c> | <c>tool_call</c> | <c>waiting_confirmation</c> |
    /// <c>finishing</c> | <c>interrupted</c>.
    /// </summary>
    public string Phase { get; init; } = "";

    /// <summary>Milliseconds elapsed since the run started.</summary>
    public long ElapsedMs { get; init; }

    /// <summary>Tool calls executed so far.</summary>
    public int ToolsUsed { get; init; }

    /// <summary>Distinct workspace paths touched so far.</summary>
    public int FilesTouched { get; init; }
}

/// <summary>
/// Human-readable wrap-up emitted on <c>finish</c> (kind
/// <c>work_summary</c>, SPEC-24 RF-003): LLM-drafted over the run's
/// actions/observations, deterministic fallback without a summarizer.
/// </summary>
public sealed record WorkSummaryEvent : EventBase
{
    /// <summary>Creates an agent-sourced work summary.</summary>
    public WorkSummaryEvent() => Source = EventSource.Agent;

    /// <summary>Summary prose (redacted).</summary>
    public string Summary { get; init; } = "";

    /// <summary>Workspace paths changed during the run.</summary>
    public IReadOnlyList<string> FilesChanged { get; init; } = [];

    /// <summary>Number of commands executed.</summary>
    public int CommandsRun { get; init; }

    /// <summary>Total run duration in ms.</summary>
    public long DurationMs { get; init; }

    /// <summary>Test counters when inferable.</summary>
    public int? TestsPassed { get; init; }
    /// <summary>Test counters when inferable.</summary>
    public int? TestsFailed { get; init; }
}

/// <summary>
/// User-visible run diagnostic (kind <c>agent_diagnostic</c>, SPEC-24 RF-003):
/// retries, model fallbacks, truncation, stale agent — the run's own log.
/// </summary>
public sealed record AgentDiagnosticEvent : EventBase
{
    /// <summary>Creates an environment-sourced diagnostic.</summary>
    public AgentDiagnosticEvent() => Source = EventSource.Environment;

    /// <summary><c>info</c> | <c>warn</c> | <c>error</c>.</summary>
    public string Severity { get; init; } = "info";

    /// <summary>Stable machine code (llm_retry, model_fallback, …).</summary>
    public string Code { get; init; } = "";

    /// <summary>Human-readable detail (redacted).</summary>
    public string Message { get; init; } = "";
}
