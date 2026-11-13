namespace Sandcastle.Application.Eval;

/// <summary>
/// SPEC-152 RF-002/RF-004 — <c>Eval:*</c> configuration
/// (<c>SANDCASTLE_Eval__*</c> env vars). Cost guardrails are hard caps;
/// runner knobs bound the per-task pipeline.
/// </summary>
public sealed class EvalOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Eval";

    /// <summary>Tasks executing in parallel inside one run (default 4).</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Absolute cap of concurrent eval tasks across runs.</summary>
    public int MaxConcurrencyCap { get; set; } = 8;

    /// <summary>Default per-task wall-clock bound in seconds (default 1800).</summary>
    public int TaskTimeoutSec { get; set; } = 1800;

    /// <summary>Per-task ReAct bound — overrides agent max_iterations.</summary>
    public int MaxTurns { get; set; } = 100;

    /// <summary>Per-setup-command bound in seconds.</summary>
    public int SetupCmdTimeoutSec { get; set; } = 300;

    /// <summary>Checker execution bound in seconds (default 600).</summary>
    public int CheckerTimeoutSec { get; set; } = 600;

    /// <summary>READY-wait bound in seconds (sandbox provisioning).</summary>
    public int ProvisionTimeoutSec { get; set; } = 900;

    /// <summary>Poll cadence while waiting on conversations.</summary>
    public int PollIntervalSec { get; set; } = 10;

    /// <summary>Hard cap of tasks per run (SPEC §RF-004 — default 200).</summary>
    public int MaxTasks { get; set; } = 200;

    /// <summary>
    /// Org daily eval spend cap in USD — sum of
    /// <c>eval_results.cost_usd</c> per UTC day; 0/null = unbounded.
    /// </summary>
    public decimal? MaxDailyCostUsd { get; set; }

    /// <summary>
    /// Runs with more tasks than this require an explicit estimate
    /// acknowledgement (<c>estimate_acknowledged</c>) — SPEC §9 (default 25).
    /// </summary>
    public int EstimateGateTasks { get; set; } = 25;

    /// <summary>
    /// Dataset cache root — <c>terminal-bench-2</c> adapter resolves
    /// <c>{DatasetCacheDir}/{suite-or-dataset}</c> to task dirs. Datasets
    /// are downloaded out-of-band (<c>eval datasets fetch</c> / manual) and
    /// NEVER committed to the repo (license guardrail).
    /// </summary>
    public string DatasetCacheDir { get; set; } = "eval-cache";

    /// <summary>Fallback avg prompt tokens per task (no history).</summary>
    public long EstimatedPromptTokensPerTask { get; set; } = 200_000;

    /// <summary>Fallback avg completion tokens per task.</summary>
    public long EstimatedCompletionTokensPerTask { get; set; } = 40_000;

    /// <summary>Effective per-run parallelism — clamped to the hard cap.</summary>
    public int EffectiveConcurrency =>
        Math.Clamp(MaxConcurrency, 1, Math.Max(1, MaxConcurrencyCap));
}