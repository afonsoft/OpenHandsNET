namespace Sandcastle.Infrastructure.Refactors;

/// <summary>
/// SPEC-144 RF-001/RF-002/NFR: <c>Refactor:*</c> configuration. The
/// approval gate is a hard default (a planner-produced plan is untrusted
/// until a human reviews it); concurrency is bounded by
/// <see cref="MaxConcurrencyCap"/> (never above the delegation sibling
/// cap of 8).
/// </summary>
public sealed class RefactorOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Refactor";

    /// <summary>
    /// Hard approval gate — POST /{id}/start requires
    /// <c>perm:refactors.approve</c> (default true; §9 guardrail).
    /// </summary>
    public bool RequirePlanApproval { get; set; } = true;

    /// <summary>Default max concurrent batch children (spec default 5).</summary>
    public int DefaultMaxConcurrency { get; set; } = 5;

    /// <summary>Hard cap — never exceed the delegation sibling cap (8).</summary>
    public int MaxConcurrencyCap { get; set; } = 8;

    /// <summary>Verify→fix loop cap inside a batch child (spec = 5).</summary>
    public int MaxVerifyFixLoops { get; set; } = 5;

    /// <summary>Default files-per-batch target when no hint is given.</summary>
    public int DefaultBatchSize { get; set; } = 20;

    /// <summary>Hard cap of batches per plan (guardrail).</summary>
    public int MaxBatches { get; set; } = 64;

    /// <summary>Max files per batch — larger batches get split regardless.</summary>
    public int MaxBatchFiles { get; set; } = 200;

    /// <summary>Max repositories per plan (bounded by multi-repo cap 4).</summary>
    public int MaxRepos { get; set; } = 4;

    /// <summary>Poll cadence while waiting on batch children.</summary>
    public int PollIntervalSeconds { get; set; } = 15;

    /// <summary>Per-child timeout — stalled children fail the batch.</summary>
    public int ChildTimeoutMinutes { get; set; } = 60;

    /// <summary>Effective concurrency for a request value (clamp 1..cap).</summary>
    public int EffectiveConcurrency(int? requested) =>
        Math.Clamp(requested ?? DefaultMaxConcurrency,
            1, Math.Max(1, MaxConcurrencyCap));
}