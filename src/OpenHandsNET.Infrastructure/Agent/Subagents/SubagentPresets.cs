namespace OpenHandsNET.Infrastructure.Agent.Subagents;

/// <summary>
/// SPEC-28 RF-001: server-side allowlists per <c>subagent_type</c>.
/// The child registry is filtered here — never by prompt instruction —
/// so an <c>explore</c> child physically cannot mutate the workspace.
/// <c>task</c>/<c>task_status</c>/<c>task_result</c> are always excluded
/// from children (depth cap).
/// </summary>
public static class SubagentPresets
{
    private static readonly HashSet<string> Reserved = new(
        StringComparer.Ordinal) { "task", "task_status", "task_result" };

    private static readonly HashSet<string> Explore = new(
        StringComparer.Ordinal)
        { "search", "file_read", "bash", "think", "finish" };

    private static readonly HashSet<string> Scout = new(
        StringComparer.Ordinal)
        { "web_fetch", "search", "file_read", "think", "finish" };

    private static readonly HashSet<string> Plan = new(
        StringComparer.Ordinal)
        { "search", "file_read", "think", "task_tracker", "finish" };

    /// <summary>Resolves the allowlist predicate for <paramref name="type"/>.</summary>
    public static Func<string, bool>? Resolve(string? type) =>
        (type ?? "explore").ToLowerInvariant() switch
        {
            "explore" => n => Explore.Contains(n),
            "scout" => n => Scout.Contains(n),
            "plan" => n => Plan.Contains(n),
            "general" => n => !Reserved.Contains(n),
            _ => null,
        };

    /// <summary>Whether <paramref name="type"/> is a known preset.</summary>
    public static bool IsKnown(string? type) => Resolve(type) is not null;
}
