namespace Sandcastle.Domain.Events;

/// <summary>
/// Workspace file-hook execution record (kind <c>hook_fired</c>) — SPEC-127.
/// Emitted once per <c>.openhands/hooks/*.hook.json</c> hook that fired at a
/// lifecycle point. Distinct from <see cref="HookExecutionEvent"/>, which is
/// the audit record of DB-configured hooks (SPEC-11).
/// </summary>
public sealed record HookFiredEvent : EventBase
{
    public HookFiredEvent() => Source = EventSource.Hook;

    /// <summary>Lifecycle point (canonical SC name: on_tool_pre…).</summary>
    public required string HookEventType { get; init; }

    /// <summary>Hook file name inside <c>.openhands/hooks/</c> (audit).</summary>
    public required string HookName { get; init; }

    /// <summary>Layer of the hook file: <c>repo</c> or <c>user</c>.</summary>
    public required string Layer { get; init; }

    /// <summary>Glob matched against the tool name; null = all tools.</summary>
    public string? Matcher { get; init; }

    /// <summary><c>command</c> (shell in sandbox) or <c>prompt</c> (context injection).</summary>
    public required string HookType { get; init; }

    public string? ToolName { get; init; }
    public bool Success { get; init; }
    public int? ExitCode { get; init; }

    /// <summary>Blocking hook that failed → vetoes the step (opt-in).</summary>
    public bool Blocking { get; init; }
    public string? Error { get; init; }
    public long DurationMs { get; init; }
}