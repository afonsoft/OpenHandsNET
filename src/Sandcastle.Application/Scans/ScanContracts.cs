using Sandcastle.Application.Agent;

namespace Sandcastle.Application.Scans;

/// <summary>
/// SPEC-143 RF-001: builtin scan profiles (wire values). <c>custom</c> =
/// free-form goal without a preset checklist.
/// </summary>
public static class ScanProfiles
{
    /// <summary>Vulnerability-oriented sweep.</summary>
    public const string Security = "security";

    /// <summary>Hot-path / allocation / query anti-patterns.</summary>
    public const string Performance = "performance";

    /// <summary>Unreachable or unused code.</summary>
    public const string DeadCode = "dead-code";

    /// <summary>Logic paths lacking test coverage.</summary>
    public const string CoverageGaps = "coverage-gaps";

    /// <summary>Accessibility issues in UI code.</summary>
    public const string A11y = "a11y";

    /// <summary>Free-form goal (default when omitted).</summary>
    public const string Custom = "custom";

    /// <summary>All valid profile values.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        { Security, Performance, DeadCode, CoverageGaps, A11y, Custom };

    /// <summary>Normalized profile or <see langword="null"/> when unknown.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Custom;
        }

        var p = raw.Trim().ToLowerInvariant();
        return All.Contains(p) ? p : null;
    }
}

/// <summary>
/// SPEC-143 RF-001: scan effort → fan-out cap. The effort matrix caps the
/// number of child conversations a scan may spawn
/// (<c>Scan:EffortChildren</c> config; defaults here are the spec values).
/// </summary>
public static class ScanEfforts
{
    /// <summary>Up to 4 scan children.</summary>
    public const string Low = "low";

    /// <summary>Up to 12 scan children.</summary>
    public const string Medium = "medium";

    /// <summary>Up to 24 scan children.</summary>
    public const string High = "high";

    /// <summary>All valid effort values.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Low, Medium, High };

    /// <summary>Normalized effort or <see langword="null"/> when unknown.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Medium;
        }

        var e = raw.Trim().ToLowerInvariant();
        return All.Contains(e) ? e : null;
    }

    /// <summary>Default child cap per effort (config-overridable).</summary>
    public static int DefaultMaxChildren(string effort) => effort switch
    {
        Low => 4,
        High => 24,
        _ => 12,
    };
}

/// <summary>SPEC-143: scan lifecycle statuses (wire values).</summary>
public static class ScanStatuses
{
    /// <summary>Row created, waiting for the scan worker.</summary>
    public const string Queued = "queued";

    /// <summary>Orchestrator fanning out / collecting.</summary>
    public const string Running = "running";

    /// <summary>All children collected; findings persisted.</summary>
    public const string Completed = "completed";

    /// <summary>Orchestration failed (infra / all repos unreachable).</summary>
    public const string Failed = "failed";

    /// <summary>Terminal statuses.</summary>
    public static readonly IReadOnlySet<string> Terminal =
        new HashSet<string>(StringComparer.Ordinal) { Completed, Failed, Cancelled };

    /// <summary>Cancelled by a future cancel endpoint (reserved).</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>SPEC-143 RF-003: finding triage status (wire values).</summary>
public static class ScanFindingStatuses
{
    /// <summary>New, untriaged finding.</summary>
    public const string Open = "open";

    /// <summary>Human reviewed — accepted as real work.</summary>
    public const string Reviewed = "reviewed";

    /// <summary>False positive / won't fix.</summary>
    public const string Dismissed = "dismissed";

    /// <summary>All valid values.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Open, Reviewed, Dismissed };
}

/// <summary>
/// SPEC-143 RF-003: remediation pipeline (Devin-parity):
/// unassigned → assigned (fix session) → pr_open → merged.
/// </summary>
public static class ScanRemediationStates
{
    /// <summary>No fix session started.</summary>
    public const string Unassigned = "unassigned";

    /// <summary>Fix conversation created and running.</summary>
    public const string Assigned = "assigned";

    /// <summary>The fix conversation opened a PR.</summary>
    public const string PrOpen = "pr_open";

    /// <summary>The fix PR was merged.</summary>
    public const string Merged = "merged";

    /// <summary>All valid values.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        { Unassigned, Assigned, PrOpen, Merged };
}

/// <summary>SPEC-143 RF-003: finding kinds (wire values).</summary>
public static class ScanFindingKinds
{
    /// <summary>Security issue — permission-gated (scans.findings).</summary>
    public const string Security = "security";

    /// <summary>Performance issue.</summary>
    public const string Performance = "performance";

    /// <summary>Dead/unreachable code.</summary>
    public const string DeadCode = "dead_code";

    /// <summary>Coverage gap.</summary>
    public const string Coverage = "coverage";

    /// <summary>Accessibility issue.</summary>
    public const string A11y = "a11y";

    /// <summary>Free-form/custom finding.</summary>
    public const string Custom = "custom";

    /// <summary>All valid values.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        { Security, Performance, DeadCode, Coverage, A11y, Custom };

    /// <summary>Default kind emitted for a scan profile.</summary>
    public static string ForProfile(string profile) => profile switch
    {
        ScanProfiles.Security => Security,
        ScanProfiles.Performance => Performance,
        ScanProfiles.DeadCode => DeadCode,
        ScanProfiles.CoverageGaps => Coverage,
        ScanProfiles.A11y => A11y,
        _ => Custom,
    };

    /// <summary>Normalized kind or <see langword="null"/> when unknown.</summary>
    public static string? Normalize(string? raw)
    {
        var k = raw?.Trim().ToLowerInvariant();
        return k is not null && All.Contains(k) ? k : null;
    }
}

/// <summary>SPEC-143 RF-003: severities ordered high→low for dedup merges.</summary>
public static class ScanSeverities
{
    /// <summary>Critical.</summary>
    public const string Critical = "critical";

    /// <summary>High.</summary>
    public const string High = "high";

    /// <summary>Medium.</summary>
    public const string Medium = "medium";

    /// <summary>Low.</summary>
    public const string Low = "low";

    /// <summary>Informational.</summary>
    public const string Info = "info";

    /// <summary>All valid values.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        { Critical, High, Medium, Low, Info };

    /// <summary>Normalized severity (<see cref="Medium"/> fallback).</summary>
    public static string NormalizeOrMedium(string? raw)
    {
        var s = raw?.Trim().ToLowerInvariant();
        return s is not null && All.Contains(s) ? s : Medium;
    }

    /// <summary>Rank for severity-keeping merges (lower = more severe).</summary>
    public static int Rank(string severity) => severity switch
    {
        Critical => 0,
        High => 1,
        Medium => 2,
        Low => 3,
        _ => 4,
    };
}

/// <summary>SPEC-143 RF-003: effort-vs-impact payoff estimate.</summary>
public static class ScanPayoffs
{
    /// <summary>High payoff.</summary>
    public const string High = "high";

    /// <summary>Medium payoff.</summary>
    public const string Medium = "medium";

    /// <summary>Low payoff.</summary>
    public const string Low = "low";

    /// <summary>Normalized payoff or <see langword="null"/>.</summary>
    public static string? Normalize(string? raw)
    {
        var p = raw?.Trim().ToLowerInvariant();
        return p is High or Medium or Low ? p : null;
    }
}

/// <summary>SPEC-143 RF-002: agent_type values used by scan conversations.</summary>
public static class ScanAgentTypes
{
    /// <summary>Coordinator shell — parents scan children via delegate.</summary>
    public const string Coordinator = "scan-coordinator";

    /// <summary>Read-only partition worker.</summary>
    public const string Worker = "scan-worker";

    /// <summary>Remediation (fix) conversation.</summary>
    public const string Fix = "scan-fix";
}

/// <summary>SPEC-143: how the scan was triggered (audit/stats).</summary>
public static class ScanTriggers
{
    /// <summary>POST /scans or Scans UI.</summary>
    public const string Manual = "manual";

    /// <summary>Scheduled automation (kind=scan).</summary>
    public const string Automation = "automation";

    /// <summary>/scan slash command in the composer.</summary>
    public const string Slash = "slash";
}

/// <summary>
/// SPEC-143 RF-002/§9: the read-only tool profile applied to the
/// coordinator conversation — every scan child inherits these rules via
/// <c>IDelegationService</c> (deny rules never relax on delegation).
/// Mutation/exec surfaces are removed from the catalog entirely.
/// </summary>
public static class ScanPermissions
{
    /// <summary>Deny list — scan children can read but never mutate.</summary>
    public static readonly IReadOnlyList<AgentPermissionRule> ReadOnly =
    [
        new("file_write", PermissionDecision.Deny),
        new("file_edit", PermissionDecision.Deny),
        new("apply_patch", PermissionDecision.Deny),
        new("notebook_edit", PermissionDecision.Deny),
        new("git_commit", PermissionDecision.Deny),
        new("create_pull_request", PermissionDecision.Deny),
        new("worktree_add", PermissionDecision.Deny),
        new("worktree_merge", PermissionDecision.Deny),
        new("checkpoint", PermissionDecision.Deny),
        new("bash", PermissionDecision.Deny),
        new("browser", PermissionDecision.Deny),
        new("job_*", PermissionDecision.Deny),
        new("routine", PermissionDecision.Deny),
        // task spawns subagents — a "general" subagent bypasses the
        // permissioned catalog, so the whole subtree is denied.
        new("task", PermissionDecision.Deny),
        new("task_status", PermissionDecision.Deny),
        new("task_result", PermissionDecision.Deny),
        // would stall an unattended scan child.
        new("ask_user", PermissionDecision.Deny),
        new("memory_set", PermissionDecision.Deny),
        new("context_save", PermissionDecision.Deny),
        new("context_delete", PermissionDecision.Deny),
        new("specs", PermissionDecision.Deny),
        new("write_plan", PermissionDecision.Deny),
    ];
}

/// <summary>Payload of <c>POST /api/v1/scans</c> (SPEC-143 RF-001).</summary>
public sealed record CreateScanRequest(
    IReadOnlyList<string>? Repositories,
    string? Goal,
    string? Profile,
    string? Effort);

/// <summary>202 response of scan creation.</summary>
public sealed record ScanCreated(
    Guid ScanId, Guid CoordinatorConversationId, string Status);

/// <summary>Scan list item / detail (counts computed at read time).</summary>
public sealed record ScanSummary(
    Guid Id,
    Guid? OrgId,
    IReadOnlyList<string> Repositories,
    string Goal,
    string Profile,
    string Effort,
    string Status,
    string Trigger,
    Guid? CoordinatorConversationId,
    int FindingsCount,
    IReadOnlyDictionary<string, int> SeverityCounts,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

/// <summary>Cursored page of scans.</summary>
public sealed record ScanPage(
    IReadOnlyList<ScanSummary> Items, string? NextPageId);

/// <summary>SPEC-143 RF-003: a persisted finding (API view).</summary>
public sealed record ScanFindingInfo(
    Guid Id,
    Guid ScanId,
    Guid? OrgId,
    string Repository,
    string Path,
    int? StartLine,
    int? EndLine,
    string Kind,
    string Severity,
    double? Confidence,
    string? Title,
    string Description,
    string? RemediationHint,
    string Status,
    string RemediationState,
    Guid? ConversationId,
    string? PrUrl,
    string? Payoff,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Finding extracted from a scan child's <c>scan_finding_v1</c> output —
/// pre-persistence shape used by the orchestrator + dedup.
/// </summary>
public sealed record ScanFindingInput(
    string Repository,
    string Path,
    int? StartLine,
    int? EndLine,
    string Kind,
    string Severity,
    double? Confidence,
    string? Title,
    string Description,
    string? RemediationHint,
    string? Payoff);

/// <summary>
/// SPEC-143 RF-004: query for the findings feed — filters map 1:1 to the
/// Devin v3 parity surface (scan_id/repository/severity/status/kind/
/// remediation_state + free-text q + cursor after/first≤200).
/// </summary>
public sealed record ScanFindingQuery(
    Guid? ScanId = null,
    string? Repository = null,
    string? Severity = null,
    string? Status = null,
    string? Kind = null,
    string? RemediationState = null,
    string? Q = null,
    string? After = null,
    int? First = null);

/// <summary>Cursored page of findings.</summary>
public sealed record ScanFindingPage(
    IReadOnlyList<ScanFindingInfo> Items, string? NextPageId);

/// <summary>Result of a single fix dispatch (also the bulk item shape).</summary>
public sealed record ScanFixResult(
    Guid FindingId, Guid? ConversationId, string? Error)
{
    /// <summary>Fix conversation created.</summary>
    public static ScanFixResult Ok(Guid findingId, Guid conversationId) =>
        new(findingId, conversationId, null);

    /// <summary>Fix rejected (not_found | fix_in_progress | quota…).</summary>
    public static ScanFixResult Fail(Guid findingId, string error) =>
        new(findingId, null, error);
}

/// <summary>
/// SPEC-143: scan service — creation (POST /scans + automation kind=scan),
/// org-scoped reads, finding triage, fix dispatch and the remediation
/// reconciliation pass (assigned → pr_open → merged).
/// </summary>
public interface IScanService
{
    /// <summary>
    /// Creates a scan: validates the request, spawns the coordinator
    /// conversation (agent_type <c>scan-coordinator</c>, read-only
    /// permission profile) and enqueues the orchestration job. The tuple's
    /// second element carries the validation message on failure.
    /// </summary>
    Task<(ScanCreated? Created, string? Error)> CreateAsync(
        CreateScanRequest request, Guid ownerUserId, Guid? orgId,
        string trigger, CancellationToken ct);

    /// <summary>Scan detail (org-scoped; null = not found).</summary>
    Task<ScanSummary?> GetAsync(Guid orgId, Guid scanId, CancellationToken ct);

    /// <summary>Org scan list — newest first, cursored.</summary>
    Task<ScanPage> ListAsync(
        Guid orgId, string? status, string? cursor, int limit,
        CancellationToken ct);

    /// <summary>Findings feed with the RF-004 filter set + cursor.</summary>
    Task<ScanFindingPage> SearchFindingsAsync(
        Guid orgId, ScanFindingQuery query, CancellationToken ct);

    /// <summary>Single finding (org-scoped; null = not found).</summary>
    Task<ScanFindingInfo?> GetFindingAsync(
        Guid orgId, Guid findingId, CancellationToken ct);

    /// <summary>
    /// Triage PATCH — <paramref name="status"/> ∈ open|reviewed|dismissed.
    /// Null = not found; invalid values throw
    /// <see cref="ArgumentException"/> (400 at the edge).
    /// </summary>
    Task<ScanFindingInfo?> UpdateFindingStatusAsync(
        Guid orgId, Guid findingId, string status, CancellationToken ct);

    /// <summary>
    /// "Fix with Sandcastle" — creates the remediation conversation
    /// (agent_type <c>scan-fix</c>, inherits org policies via the owner)
    /// and moves the finding to <c>assigned</c>. Errors:
    /// <c>not_found</c>, <c>fix_in_progress</c>.
    /// </summary>
    Task<ScanFixResult> FixAsync(
        Guid orgId, Guid findingId, Guid actorUserId, CancellationToken ct);

    /// <summary>Bulk fix — per-finding results; cap enforced by caller.</summary>
    Task<IReadOnlyList<ScanFixResult>> FixBulkAsync(
        Guid orgId, IReadOnlyList<Guid> ids, Guid actorUserId,
        CancellationToken ct);

    /// <summary>
    /// Reconciliation pass: <c>assigned</c> findings whose conversation
    /// opened a PR → <c>pr_open</c>; <c>pr_open</c> whose PR merged →
    /// <c>merged</c>; dead fix sessions / closed-unmerged PRs → back to
    /// <c>unassigned</c> (explicit user action can re-fix).
    /// </summary>
    Task ReconcileRemediationsAsync(CancellationToken ct);
}

/// <summary>SPEC-143 RF-002: in-process queue of scan jobs.</summary>
public interface IScanDispatcher
{
    /// <summary>Enqueues a scan for orchestration.</summary>
    ValueTask EnqueueAsync(Guid scanId, CancellationToken ct);
}

/// <summary>
/// SPEC-143 RF-002: the real orchestrator that replaces the
/// <c>SwarmOrchestrator</c> stub — partitions repos by directory, fans out
/// scan-worker children via <see cref="Delegation.IDelegationService"/>,
/// collects <c>scan_finding_v1</c> payloads, dedups and persists findings.
/// </summary>
public interface IScanOrchestrator
{
    /// <summary>Executes one scan (idempotent per scan id).</summary>
    Task RunAsync(Guid scanId, CancellationToken ct);
}