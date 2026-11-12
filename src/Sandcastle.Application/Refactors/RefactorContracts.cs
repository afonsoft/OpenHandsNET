namespace Sandcastle.Application.Refactors;

/// <summary>
/// SPEC-144 RF-001: planner strategies (wire values).
/// <c>import-graph</c> builds the dependency graph from import analysis
/// (C# <c>using</c>/project refs first; TS/JS + Python import heuristics)
/// and falls back to <c>directory</c> frontier bucketing per repo when the
/// graph is empty or the language is unsupported.
/// </summary>
public static class RefactorStrategies
{
    /// <summary>Directory-frontier buckets (always available).</summary>
    public const string Directory = "directory";

    /// <summary>Import-analysis graph, topologically ordered batches.</summary>
    public const string ImportGraph = "import-graph";

    /// <summary>All valid strategy values.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        { Directory, ImportGraph };

    /// <summary>Normalized strategy or <see langword="null"/> when unknown.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ImportGraph;
        }

        var s = raw.Trim().ToLowerInvariant();
        return All.Contains(s) ? s : null;
    }
}

/// <summary>
/// SPEC-144 RF-002: PR base modes (wire values). <c>independent</c> = every
/// batch PR targets the default branch (requires zero file overlap);
/// <c>stacked</c> = each PR bases on the previous batch's head branch
/// (GitHub-only; the planner emits a linear depends_on chain so batches
/// serialize in topological order).
/// </summary>
public static class RefactorModes
{
    /// <summary>PRs based on the repo default branch (default).</summary>
    public const string Independent = "independent";

    /// <summary>PRs chained — base of n = head of n-1 (GitHub-only).</summary>
    public const string Stacked = "stacked";

    /// <summary>All valid mode values.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        { Independent, Stacked };

    /// <summary>Normalized mode or <see langword="null"/> when unknown.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Independent;
        }

        var m = raw.Trim().ToLowerInvariant();
        return All.Contains(m) ? m : null;
    }
}

/// <summary>SPEC-144 RF-001/RF-003: plan lifecycle statuses (wire values).</summary>
public static class RefactorPlanStatuses
{
    /// <summary>Planner is building the dependency graph / batches.</summary>
    public const string Planning = "planning";

    /// <summary>Plan persisted — waiting the human approval gate.</summary>
    public const string AwaitingApproval = "awaiting_approval";

    /// <summary>Approved/started — batches dispatch under the coordinator.</summary>
    public const string Running = "running";

    /// <summary>Dispatch paused by the user (in-flight children continue).</summary>
    public const string Paused = "paused";

    /// <summary>Every batch merged.</summary>
    public const string Completed = "completed";

    /// <summary>Planning failed or the executor gave up unrecoverably.</summary>
    public const string Failed = "failed";

    /// <summary>Cancelled by the user (terminal).</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Terminal statuses.</summary>
    public static readonly IReadOnlySet<string> Terminal =
        new HashSet<string>(StringComparer.Ordinal)
        { Completed, Failed, Cancelled };
}

/// <summary>
/// SPEC-144 §4: batch states (wire values).
/// <c>pending</c> → deps not all merged; <c>ready</c> → eligible for
/// dispatch; <c>running</c> → child delegated; <c>verifying</c> → child
/// terminal, output/PR being validated; <c>pr_open</c> → PR opened;
/// <c>merged</c> → PR merged (deps may unblock); <c>failed</c> → verify
/// loop exhausted / child died (retryable); <c>blocked</c> → a dependency
/// failed (returns to <c>pending</c> on dep retry).
/// </summary>
public static class RefactorBatchStates
{
    /// <summary>Waiting on dependencies.</summary>
    public const string Pending = "pending";

    /// <summary>Dependencies merged — dispatchable.</summary>
    public const string Ready = "ready";

    /// <summary>Child conversation running.</summary>
    public const string Running = "running";

    /// <summary>Child terminal — verify output/PR being checked.</summary>
    public const string Verifying = "verifying";

    /// <summary>PR opened for the batch branch.</summary>
    public const string PrOpen = "pr_open";

    /// <summary>PR merged — dependents may unblock.</summary>
    public const string Merged = "merged";

    /// <summary>Verify loop exhausted / child failed (retryable).</summary>
    public const string Failed = "failed";

    /// <summary>A dependency failed — back to pending on dep retry.</summary>
    public const string Blocked = "blocked";

    /// <summary>All valid values.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Pending, Ready, Running, Verifying, PrOpen,
            Merged, Failed, Blocked,
        };

    /// <summary>States that count as "in flight" for concurrency caps.</summary>
    public static readonly IReadOnlySet<string> Active =
        new HashSet<string>(StringComparer.Ordinal)
        { Running, Verifying };

    /// <summary>States that never re-dispatch on their own.</summary>
    public static readonly IReadOnlySet<string> Terminal =
        new HashSet<string>(StringComparer.Ordinal)
        { Merged };
}

/// <summary>SPEC-144: risk labels emitted by the planner (wire values).</summary>
public static class RefactorRisks
{
    /// <summary>Low risk.</summary>
    public const string Low = "low";

    /// <summary>Medium risk.</summary>
    public const string Medium = "med";

    /// <summary>High risk.</summary>
    public const string High = "high";

    /// <summary>All valid values.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Low, Medium, High };

    /// <summary>Normalized risk (<see cref="Low"/> fallback).</summary>
    public static string NormalizeOrLow(string? raw)
    {
        var r = raw?.Trim().ToLowerInvariant();
        return r is not null && All.Contains(r) ? r : Low;
    }
}

/// <summary>SPEC-144: agent_type values used by refactor conversations.</summary>
public static class RefactorAgentTypes
{
    /// <summary>Planner shell — owns the produced plan.</summary>
    public const string Planner = "refactor-planner";

    /// <summary>Coordinator shell — parents batch children via delegate.</summary>
    public const string Coordinator = "refactor-coordinator";

    /// <summary>Batch worker child (edits + verify + PR).</summary>
    public const string Worker = "refactor-worker";
}

/// <summary>Payload of <c>POST /api/v1/refactors</c> (SPEC-144 RF-001).</summary>
public sealed record CreateRefactorRequest(
    IReadOnlyList<string>? Repositories,
    string? Goal,
    string? Strategy,
    int? MaxConcurrency,
    string? Mode,
    int? BatchSizeHint);

/// <summary>202 response of refactor creation.</summary>
public sealed record RefactorCreated(
    Guid PlanId, Guid PlannerConversationId, string Status);

/// <summary>
/// Plan list item / detail — batch state counts computed at read time.
/// </summary>
public sealed record RefactorPlanSummary(
    Guid Id,
    Guid? OrgId,
    IReadOnlyList<string> Repositories,
    string Goal,
    string Strategy,
    string Mode,
    string? RequestedMode,
    int MaxConcurrency,
    int? BatchSizeHint,
    string Status,
    int BatchesTotal,
    IReadOnlyDictionary<string, int> StateCounts,
    int ProgressPercent,
    Guid? PlannerConversationId,
    Guid? CoordinatorConversationId,
    IReadOnlyList<string> Warnings,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

/// <summary>Cursored page of refactor plans.</summary>
public sealed record RefactorPlanPage(
    IReadOnlyList<RefactorPlanSummary> Items, string? NextPageId);

/// <summary>SPEC-144 §4: a persisted batch (API view).</summary>
public sealed record RefactorBatchInfo(
    Guid Id,
    Guid PlanId,
    int N,
    string Repository,
    IReadOnlyList<string> Files,
    IReadOnlyList<int> DependsOn,
    string? Description,
    string Risk,
    string? VerifyCommand,
    string State,
    string? PrUrl,
    int? PrNumber,
    Guid? ConversationId,
    int Attempts,
    string? VerifyLogDigest,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// SPEC-144 RF-001: the planner's dependency-graph batch (pre-persistence
/// shape — also the unit inside <c>refactor_plan_v1.plan.batches[]</c>).
/// </summary>
public sealed record RefactorBatchInput(
    string Repository,
    IReadOnlyList<string> Files,
    IReadOnlyList<int> DependsOn,
    string? Description,
    string Risk,
    string? VerifyCommand);

/// <summary>
/// Output of the deterministic planner: ordered batches + the effective
/// mode (may differ from the request — overlap in <c>independent</c>
/// mode forces <c>stacked</c>) + human-surfaceable warnings.
/// </summary>
public sealed record RefactorPlanOutput(
    IReadOnlyList<RefactorBatchInput> Batches,
    string Mode,
    IReadOnlyList<string> Warnings);

/// <summary>
/// <c>refactor_batch_v1</c> — output reported by a batch child at the end
/// of its run (fenced JSON block, same convention as
/// <c>scan_finding_v1</c>).
/// </summary>
public sealed record RefactorBatchOutput(
    bool VerifyPassed,
    int? VerifyAttempts,
    string? LogTail,
    string? Summary);

/// <summary>
/// SPEC-144: refactor service — creation (planner shell + planning job),
/// org-scoped reads, the approval-gated start, pause/resume/cancel,
/// batch retry, merge-webhook unblocking and the reconciliation pass.
/// </summary>
public interface IRefactorService
{
    /// <summary>
    /// Creates a refactor plan: validates the request, spawns the planner
    /// conversation (agent_type <c>refactor-planner</c>), persists the plan
    /// row (<c>planning</c>) and enqueues the planning job. The tuple's
    /// second element carries the validation message on failure.
    /// </summary>
    Task<(RefactorCreated? Created, string? Error)> CreateAsync(
        CreateRefactorRequest request, Guid ownerUserId, Guid? orgId,
        CancellationToken ct);

    /// <summary>Plan detail (org-scoped; null = not found).</summary>
    Task<RefactorPlanSummary?> GetAsync(
        Guid orgId, Guid planId, CancellationToken ct);

    /// <summary>Org plan list — newest first, cursored.</summary>
    Task<RefactorPlanPage> ListAsync(
        Guid orgId, string? status, string? cursor, int limit,
        CancellationToken ct);

    /// <summary>Batches of a plan (ordered by n; null = plan not found).</summary>
    Task<IReadOnlyList<RefactorBatchInfo>?> ListBatchesAsync(
        Guid orgId, Guid planId, CancellationToken ct);

    /// <summary>
    /// Approval gate + start: <c>awaiting_approval</c> → <c>running</c>,
    /// creates the coordinator conversation and enqueues execution.
    /// <paramref name="canApprove"/> = caller holds
    /// <c>perm:refactors.approve</c> — enforced only when
    /// <c>Refactor:RequirePlanApproval</c> is on. Errors:
    /// <c>not_found</c>, <c>approval_required</c>, <c>invalid_state</c>,
    /// <c>no_plan</c>.
    /// </summary>
    Task<string?> StartAsync(
        Guid orgId, Guid planId, Guid actorUserId, bool canApprove,
        CancellationToken ct);

    /// <summary><c>running</c> → <c>paused</c>. Errors: not_found|invalid_state.</summary>
    Task<string?> PauseAsync(Guid orgId, Guid planId, CancellationToken ct);

    /// <summary><c>paused</c> → <c>running</c> + re-enqueue. Errors: not_found|invalid_state.</summary>
    Task<string?> ResumeAsync(Guid orgId, Guid planId, CancellationToken ct);

    /// <summary>Non-terminal → <c>cancelled</c>. Errors: not_found|invalid_state.</summary>
    Task<string?> CancelAsync(Guid orgId, Guid planId, CancellationToken ct);

    /// <summary>
    /// Re-runs one batch: <c>failed</c> → <c>ready</c> (blocked dependents
    /// back to <c>pending</c>) and re-enqueues the plan. Errors:
    /// <c>not_found</c>, <c>batch_not_found</c>, <c>invalid_state</c>.
    /// </summary>
    Task<string?> RetryBatchAsync(
        Guid orgId, Guid planId, int batchN, CancellationToken ct);

    /// <summary>
    /// Merge webhook hook (SPEC-144 RF-002): a <c>pull_request</c>
    /// closed+merged event marks the matching batch <c>merged</c> and
    /// re-enqueues the plan so dependents unblock. Returns true when a
    /// batch was updated.
    /// </summary>
    Task<bool> OnPullRequestMergedAsync(
        Guid orgId, string repository, int prNumber, CancellationToken ct);

    /// <summary>
    /// PR feedback hook (SPEC-144 RF-002): forwards review feedback to the
    /// batch child (<c>delegate_message</c>) for rebase + re-verify when
    /// the child is still alive, else records a deferral note. Returns
    /// true when the PR belongs to a refactor batch.
    /// </summary>
    Task<bool> OnPullRequestFeedbackAsync(
        Guid orgId, string repository, int prNumber, string feedback,
        CancellationToken ct);

    /// <summary>
    /// Reconciliation pass (worker tick): <c>pr_open</c> batches polled
    /// against the provider → merged unblocks dependents / closed without
    /// merge fails the batch; stale <c>verifying</c> rows are re-checked.
    /// </summary>
    Task ReconcileAsync(CancellationToken ct);
}

/// <summary>SPEC-144 RF-001/RF-002: in-process queue of refactor work items.</summary>
public interface IRefactorDispatcher
{
    /// <summary>Enqueues a planning or execution run for a plan.</summary>
    ValueTask EnqueueAsync(Guid planId, RefactorWorkKind kind,
        CancellationToken ct);
}

/// <summary>Work item kinds drained by the refactor worker.</summary>
public enum RefactorWorkKind
{
    /// <summary>Build the plan (dependency graph → batches).</summary>
    Plan,

    /// <summary>Dispatch/collect batch children.</summary>
    Execute,
}

/// <summary>
/// SPEC-144 RF-001: builds <c>refactor_plan_v1</c> for one plan —
/// acquires repo content, runs the dependency-graph planner, persists
/// batches and moves the plan to <c>awaiting_approval</c> (or straight to
/// execution when the approval gate is disabled).
/// </summary>
public interface IRefactorPlanner
{
    /// <summary>Executes one planning run (idempotent per plan id).</summary>
    Task RunAsync(Guid planId, CancellationToken ct);
}

/// <summary>
/// SPEC-144 RF-002: drives the batch state machine for one plan —
/// dependency evaluation, bounded-concurrency delegate fan-out,
/// verify-gate collection and PR tracking.
/// </summary>
public interface IBatchExecutor
{
    /// <summary>Executes one orchestration pass (idempotent per plan id).</summary>
    Task RunAsync(Guid planId, CancellationToken ct);
}