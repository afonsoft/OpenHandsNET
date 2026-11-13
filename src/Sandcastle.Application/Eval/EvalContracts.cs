using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sandcastle.Application.Eval;

/// <summary>
/// SPEC-152 RF-001 — <c>eval_task_v1</c>: the canonical task contract
/// every suite adapter normalizes into
/// (<c>swe-lite</c> embedded jsonl, <c>terminal-bench-2</c> dataset dirs,
/// <c>custom</c> user jsonl).
/// </summary>
public sealed record EvalTaskV1
{
    /// <summary>Stable task key inside the suite (unique per run).</summary>
    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    /// <summary>Repository to clone (<c>owner/repo</c>) — XOR <see cref="Image"/>.</summary>
    [JsonPropertyName("repo")]
    public string? Repo { get; init; }

    /// <summary>Container image for the ephemeral sandbox — XOR <see cref="Repo"/>.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary>Commands run inside the sandbox before the agent starts.</summary>
    [JsonPropertyName("setup_cmds")]
    public IReadOnlyList<string> SetupCmds { get; init; } = [];

    /// <summary>Instruction handed to the agent (no steering afterwards).</summary>
    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    /// <summary>Grader — <c>cmd</c> runs in the sandbox; <c>path</c> is a script path executed via bash.</summary>
    [JsonPropertyName("checker")]
    public required EvalTaskChecker Checker { get; init; }

    /// <summary>Per-task wall-clock bound in seconds (null = <c>Eval:TaskTimeout</c>).</summary>
    [JsonPropertyName("timeout_s")]
    public int? TimeoutS { get; init; }

    /// <summary>Free-form adapter metadata (difficulty, tags, verifier_files…).</summary>
    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>
/// SPEC-152 RF-001 — checker form: exactly one of <see cref="Cmd"/>
/// (shell command, exit 0 = resolved) or <see cref="Path"/> (script file
/// inside the sandbox, run via <c>bash</c>).
/// </summary>
public sealed record EvalTaskChecker
{
    /// <summary>Shell command evaluated in the task sandbox.</summary>
    [JsonPropertyName("cmd")]
    public string? Cmd { get; init; }

    /// <summary>Script path inside the sandbox (executed with bash).</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }
}

/// <summary>SPEC-152 RF-003 — builtin suite registry keys.</summary>
public static class EvalSuites
{
    /// <summary>Curated smoke subset embedded at <c>bench/swe-lite.jsonl</c>.</summary>
    public const string SweLite = "swe-lite";

    /// <summary>Terminal-Bench 2.0 dataset adapter (<c>Eval:DatasetCacheDir</c>).</summary>
    public const string TerminalBench2 = "terminal-bench-2";

    /// <summary>User-supplied <c>eval_task_v1</c> jsonl (<c>tasks_path</c>).</summary>
    public const string Custom = "custom";

    /// <summary>All registry keys.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { SweLite, TerminalBench2, Custom };

    /// <summary>Registry membership test.</summary>
    public static bool IsValid(string? suite) =>
        suite is not null && All.Contains(suite);
}

/// <summary>SPEC-152 RF-002 — agent_type of eval conversations.</summary>
public static class EvalAgentTypes
{
    /// <summary>
    /// <c>eval</c> — the persisted <c>eval_mode</c> marker: the agent
    /// pipeline reads <c>agent_type == "eval"</c> to disable ChatOps /
    /// SCM postbacks / autofix side-effects and to cap turns.
    /// </summary>
    public const string Eval = "eval";

    /// <summary>eval_mode check on a conversation agent_type.</summary>
    public static bool IsEval(string? agentType) =>
        string.Equals(agentType, Eval, StringComparison.Ordinal);
}

/// <summary>SPEC-152 RF-001 — eval_runs status lifecycle.</summary>
public static class EvalRunStatuses
{
    /// <summary>Persisted, waiting on the worker queue.</summary>
    public const string Queued = "queued";

    /// <summary>Runner owns it — tasks dispatching/executing.</summary>
    public const string Running = "running";

    /// <summary>Every task reached a verdict — terminal.</summary>
    public const string Completed = "completed";

    /// <summary>Suite load / orchestration failure — terminal.</summary>
    public const string Failed = "failed";

    /// <summary>Cancelled via API — terminal.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>States where cancel is still a no-op.</summary>
    public static readonly IReadOnlySet<string> Terminal =
        new HashSet<string>(StringComparer.Ordinal)
        { Completed, Failed, Cancelled };
}

/// <summary>SPEC-152 RF-001 — per-task verdict vocabulary.</summary>
public static class EvalVerdicts
{
    /// <summary>Checker exited 0 — task solved.</summary>
    public const string Resolved = "resolved";

    /// <summary>Checker exited non-zero — agent finished but wrong.</summary>
    public const string Failed = "failed";

    /// <summary>Infrastructure/setup/checker error — not scored as fail.</summary>
    public const string Error = "error";

    /// <summary>Task hit its wall-clock bound — interrupted.</summary>
    public const string Timeout = "timeout";

    /// <summary>Terminal values persisted in <c>eval_results.verdict</c>.</summary>
    public static readonly IReadOnlySet<string> Terminal =
        new HashSet<string>(StringComparer.Ordinal)
        { Resolved, Failed, Error, Timeout };
}

/// <summary>Per-result row status (transient states precede the verdict).</summary>
public static class EvalResultStatuses
{
    /// <summary>Queued inside the run.</summary>
    public const string Pending = "pending";

    /// <summary>Conversation/checker in flight.</summary>
    public const string Running = "running";

    /// <summary><see cref="EvalResultInfo.Verdict"/> set — terminal.</summary>
    public const string Done = "done";
}

/// <summary><c>POST /api/v1/evals</c> body.</summary>
public sealed record CreateEvalRunRequest(
    string Suite,
    string? Model = null,
    string? RouteOverride = null,
    int? N = null,
    // SPEC-152 RF-003: suite=custom — path do jsonl eval_task_v1.
    string? TasksPath = null,
    // SPEC-152 §9: n > Eval:EstimateGateTasks exige estimate acknowledge.
    bool EstimateAcknowledged = false);

/// <summary><c>POST /api/v1/evals/estimate</c> body.</summary>
public sealed record EvalEstimateRequest(
    string Suite, int? N = null, string? Model = null,
    string? TasksPath = null);

/// <summary>Run summary (list/detail wire shape).</summary>
public sealed record EvalRunSummary(
    Guid Id,
    Guid? OrgId,
    string Suite,
    int NTasks,
    string? Model,
    string? RouteOverride,
    string Status,
    int ResolvedN,
    int FailedN,
    int ErrorN,
    int TimeoutN,
    double ScorePct,
    decimal? EstimatedCostUsd,
    decimal? ActualCostUsd,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

/// <summary>Per-task result wire row.</summary>
public sealed record EvalResultInfo(
    Guid Id,
    Guid RunId,
    string TaskId,
    string Status,
    string? Verdict,
    Guid? SessionId,
    double? DurationS,
    long Tokens,
    decimal CostUsd,
    string? LogDigest,
    string? Error,
    DateTimeOffset? FinishedAt);

/// <summary><c>POST /evals/estimate</c> response.</summary>
public sealed record EvalEstimate(
    string Suite,
    int N,
    string? Model,
    long AvgPromptTokens,
    long AvgCompletionTokens,
    decimal? EstimatedCostUsd,
    bool PricingKnown,
    bool RequiresAcknowledgement,
    string? Warning);

/// <summary><c>GET /evals/scorecard?suite=</c> row — resolved% por model/route.</summary>
public sealed record EvalScorecardRow(
    string? Model,
    string? Route,
    int Runs,
    int Tasks,
    int Resolved,
    double ResolvedPct,
    long TotalTokens,
    decimal TotalCostUsd,
    DateTimeOffset? LastRunAt);

/// <summary>Paged list of runs.</summary>
public sealed record EvalRunPage(
    IReadOnlyList<EvalRunSummary> Items, string? NextPageId);

/// <summary>Result of run creation (202 body payload).</summary>
public sealed record EvalRunCreated(
    Guid RunId, string Status, int NTasks, decimal? EstimatedCostUsd);

/// <summary>
/// SPEC-152 RF-002 — service surface consumed by the API layer.
/// Reads are org-scoped; the worker path uses
/// <c>IgnoreQueryFilters</c> inside the runner.
/// </summary>
public interface IEvalService
{
    /// <summary>
    /// Validates + persists the run (queued) and enqueues it. Error string
    /// is a stable key the endpoint maps to 400/403/409/428.
    /// </summary>
    Task<(EvalRunCreated? Created, string? Error, EvalEstimate? Gate)>
        CreateAsync(CreateEvalRunRequest request, Guid ownerUserId,
            Guid? orgId, CancellationToken ct);

    /// <summary>Run detail (org-scoped).</summary>
    Task<EvalRunSummary?> GetAsync(Guid orgId, Guid runId, CancellationToken ct);

    /// <summary>Cursor-paged run list.</summary>
    Task<EvalRunPage> ListAsync(Guid orgId, string? suite, string? status,
        string? cursor, int limit, CancellationToken ct);

    /// <summary>Per-task results of a run (null = run not found).</summary>
    Task<IReadOnlyList<EvalResultInfo>?> ListResultsAsync(
        Guid orgId, Guid runId, CancellationToken ct);

    /// <summary>Cancel a queued/running run — null on success.</summary>
    Task<string?> CancelAsync(Guid orgId, Guid runId, CancellationToken ct);

    /// <summary>Dry-run cost estimate (avg tokens × n × model pricing).</summary>
    Task<EvalEstimate?> EstimateAsync(EvalEstimateRequest request,
        Guid? orgId, CancellationToken ct);

    /// <summary>Scorecard — últimas N runs agrupadas por model/route.</summary>
    Task<IReadOnlyList<EvalScorecardRow>> ScorecardAsync(
        Guid? orgId, string? suite, int last, CancellationToken ct);
}

/// <summary>SPEC-152 RF-002 — in-process queue drained by the eval worker.</summary>
public interface IEvalDispatcher
{
    /// <summary>Enqueue a run id for execution.</summary>
    ValueTask EnqueueAsync(Guid runId, CancellationToken ct);
}

/// <summary>SPEC-152 RF-002 — per-run orchestrator (tasks → verdicts).</summary>
public interface IEvalRunner
{
    /// <summary>Drives one run to completion (idempotent re-entry).</summary>
    Task RunAsync(Guid runId, CancellationToken ct);
}

/// <summary>
/// SPEC-152 RF-003 — suite adapter: normalizes a suite into
/// <see cref="EvalTaskV1"/> tasks. Implementations: swe-lite (embedded),
/// terminal-bench-2 (dataset cache dir), custom (jsonl path).
/// </summary>
public interface IEvalSuiteAdapter
{
    /// <summary>Suite key handled by this adapter.</summary>
    string Suite { get; }

    /// <summary>Loads the task list (ordered, deduped by task_id).</summary>
    Task<IReadOnlyList<EvalTaskV1>> LoadAsync(
        EvalSuiteLoadContext ctx, CancellationToken ct);
}

/// <summary>Adapter input — dataset/custom path hints + options.</summary>
public sealed record EvalSuiteLoadContext(
    string? TasksPath,
    string? DatasetCacheDir,
    string? DatasetName = null);

/// <summary>Resolves suite key → adapter (null = unknown suite).</summary>
public interface IEvalSuiteResolver
{
    /// <summary>Adapter for a suite key.</summary>
    IEvalSuiteAdapter? Get(string suite);
}