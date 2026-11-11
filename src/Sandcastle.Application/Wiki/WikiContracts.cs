namespace Sandcastle.Application.Wiki;

/// <summary>Wiki run statuses (wire values).</summary>
public static class WikiRunStatuses
{
    /// <summary>Enqueued, waiting for the worker.</summary>
    public const string Queued = "queued";

    /// <summary>Runner in progress.</summary>
    public const string Running = "running";

    /// <summary>Pages generated (some may be stale-flagged).</summary>
    public const string Completed = "completed";

    /// <summary>Run failed (see error).</summary>
    public const string Failed = "failed";
}

/// <summary>Generation effort levels (target page counts — SPEC-145 RF-002).</summary>
public static class WikiEfforts
{
    /// <summary>~5 pages.</summary>
    public const string Low = "low";

    /// <summary>~15 pages (default).</summary>
    public const string Medium = "medium";

    /// <summary>~40 pages.</summary>
    public const string High = "high";

    /// <summary>Normalizes the value (null/unknown → medium).</summary>
    public static string Normalize(string? effort) =>
        effort?.Trim().ToLowerInvariant() switch
        {
            Low => Low,
            High => High,
            _ => Medium,
        };
}

/// <summary>
/// Per-repo wiki config read from <c>.sandcastle/wiki.json</c>
/// (SPEC-145 RF-002 — espelho de <c>.devin/wiki.json</c>).
/// </summary>
public sealed record WikiRepoConfig(
    IReadOnlyList<string>? Include = null,
    IReadOnlyList<string>? Exclude = null,
    string? Language = null,
    string? Effort = null,
    IReadOnlyList<string>? TopicsHint = null,
    bool AutoUpdate = false)
{
    /// <summary>Canonical repo path of the config file.</summary>
    public const string ConfigPath = ".sandcastle/wiki.json";
}

/// <summary>A generated wiki page (metadata view — no content).</summary>
public sealed record WikiPageInfo(
    Guid Id,
    string Topic,
    string PathKey,
    string Title,
    bool Stale,
    int SortOrder);

/// <summary>Full page payload (markdown included).</summary>
public sealed record WikiPageDto(
    Guid Id,
    string Repository,
    string Branch,
    string Topic,
    string PathKey,
    string Title,
    string ContentMd,
    IReadOnlyList<string> SourceFiles,
    bool Stale,
    Guid WikiRunId,
    DateTimeOffset UpdatedAt);

/// <summary>Topic-tree node of the wiki browser.</summary>
public sealed record WikiTreeNode(
    string Key,
    string Title,
    IReadOnlyList<WikiPageInfo> Pages);

/// <summary>Wiki tree response for one repo/branch.</summary>
public sealed record WikiTreeDto(
    string Repository,
    string Branch,
    string? CommitSha,
    IReadOnlyList<WikiTreeNode> Topics);

/// <summary>Run summary (generation job state).</summary>
public sealed record WikiRunDto(
    Guid Id,
    Guid? OrgId,
    string Repository,
    string Branch,
    string? CommitSha,
    string Effort,
    string Status,
    string? StatsJson,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

/// <summary>
/// Wiki domain service (SPEC-145 RF-002): trigger generation, read the
/// topic tree/pages, mark pages stale on push, opt-in auto-regeneration.
/// </summary>
public interface IWikiService
{
    /// <summary>
    /// Creates a wiki run for (repo, branch, org) and enqueues it —
    /// returns the queued run (null when the repo index isn't ready).
    /// </summary>
    Task<WikiRunDto?> GenerateAsync(
        string repository, string? branch, string? effort,
        Guid? orgId, string? requestedByUserId, CancellationToken ct);

    /// <summary>Topic tree of the latest completed run (empty when none).</summary>
    Task<WikiTreeDto> GetTreeAsync(
        string repository, string? branch, Guid? orgId,
        CancellationToken ct);

    /// <summary>One page by id (null = not found).</summary>
    Task<WikiPageDto?> GetPageAsync(
        Guid pageId, CancellationToken ct);

    /// <summary>One page by path_key/topic of the latest run (null = not found).</summary>
    Task<WikiPageDto?> GetPageByTopicAsync(
        string repository, string topic, string? branch, Guid? orgId,
        CancellationToken ct);

    /// <summary>Run status by id (null = not found).</summary>
    Task<WikiRunDto?> GetRunAsync(Guid runId, CancellationToken ct);

    /// <summary>
    /// Push hook (SPEC-145 RF-002): marks pages whose
    /// <c>source_files</c> intersect <paramref name="changedPaths"/>
    /// stale; when the latest run was generated with
    /// <c>auto_update</c>, enqueues a regeneration. Returns true when
    /// any page was touched or a run was enqueued.
    /// </summary>
    Task<bool> HandlePushAsync(
        string repository, string branch,
        IReadOnlyList<string> changedPaths, string? headSha,
        Guid? orgId, CancellationToken ct);
}

/// <summary>In-process queue of wiki runs (same pattern as IReviewDispatcher).</summary>
public interface IWikiDispatcher
{
    /// <summary>Enqueues a wiki run for the worker.</summary>
    ValueTask EnqueueAsync(Guid wikiRunId, CancellationToken ct);
}

/// <summary>Executes one wiki run (page planning + LLM generation).</summary>
public interface IWikiRunner
{
    /// <summary>Runs the wiki generation pipeline for the queued run.</summary>
    Task RunAsync(Guid wikiRunId, CancellationToken ct);
}