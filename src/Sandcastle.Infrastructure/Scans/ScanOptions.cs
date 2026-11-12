namespace Sandcastle.Infrastructure.Scans;

/// <summary>
/// SPEC-143 RF-001/RF-002: <c>Scan:*</c> configuration. The effort matrix
/// caps children per scan (defaults = spec values 4/12/24) and
/// <see cref="MaxSiblings"/> respects <c>Delegation:MaxSiblings</c> (8) —
/// larger fan-outs run in sequential waves.
/// </summary>
public sealed class ScanOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Scan";

    /// <summary>Child cap per effort (low|medium|high → 4|12|24).</summary>
    public Dictionary<string, int> EffortChildren { get; set; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["low"] = 4,
            ["medium"] = 12,
            ["high"] = 24,
        };

    /// <summary>Max repositories per scan (default 8).</summary>
    public int MaxRepos { get; set; } = 8;

    /// <summary>Max scan children active at once (≤ Delegation:MaxSiblings=8).</summary>
    public int MaxSiblings { get; set; } = 8;

    /// <summary>Directories per partition soft cap (prompt readability).</summary>
    public int MaxDirsPerPartition { get; set; } = 6;

    /// <summary>Poll cadence while waiting on scan children.</summary>
    public int PollIntervalSeconds { get; set; } = 15;

    /// <summary>Per-child timeout — stalled children are collected as-is.</summary>
    public int ChildTimeoutMinutes { get; set; } = 30;

    /// <summary>Optional second LLM dedup pass (role <c>analysis</c>).</summary>
    public bool LlmDedup { get; set; }

    /// <summary>Model override for the dedup pass (null = role routing).</summary>
    public string? Model { get; set; }

    /// <summary>Max findings persisted per scan (noise cap).</summary>
    public int MaxFindings { get; set; } = 500;

    /// <summary>Effective child cap for an effort value.</summary>
    public int ChildrenCapFor(string effort) =>
        EffortChildren.TryGetValue(effort, out var n) && n > 0
            ? n
            : Application.Scans.ScanEfforts.DefaultMaxChildren(effort);
}