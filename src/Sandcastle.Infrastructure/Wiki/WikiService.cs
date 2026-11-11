using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Wiki;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Wiki;

/// <summary>
/// <see cref="IWikiService"/> (SPEC-145 RF-002): trigger do run
/// (repo precisa estar indexado), leitura da árvore/páginas do último
/// run completo, e marcação stale diff-aware no push.
/// </summary>
public sealed class WikiService(
    AppDbContext db,
    IWikiDispatcher dispatcher,
    IOptions<WikiOptions> options,
    ILogger<WikiService> logger) : IWikiService
{
    /// <inheritdoc />
    public async Task<WikiRunDto?> GenerateAsync(
        string repository, string? branch, string? effort,
        Guid? orgId, string? requestedByUserId, CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            return null;
        }

        var b = branch ?? "main";
        var index = await db.RepoIndexes.IgnoreQueryFilters()
            .Where(r => r.Repository == repository && r.Branch == b
                && r.OrgId == orgId && r.Status == "ready")
            .OrderByDescending(r => r.UpdatedAtTicks)
            .FirstOrDefaultAsync(ct);
        if (index is null)
        {
            return null; // wiki precisa do índice pronto (citações válidas).
        }

        var run = new WikiRunRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Repository = repository,
            Branch = b,
            CommitSha = index.CommitSha,
            Effort = WikiEfforts.Normalize(effort),
            Status = WikiRunStatuses.Queued,
            RequestedByUserId = requestedByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WikiRuns.Add(run);
        await db.SaveChangesAsync(ct);
        await dispatcher.EnqueueAsync(run.Id, ct);
        return ToDto(run);
    }

    /// <inheritdoc />
    public async Task<WikiTreeDto> GetTreeAsync(
        string repository, string? branch, Guid? orgId,
        CancellationToken ct)
    {
        var run = await LatestCompletedRunAsync(
            repository, branch, orgId, ct);
        if (run is null)
        {
            return new WikiTreeDto(
                repository, branch ?? "main", null, []);
        }

        var pages = await db.WikiPages.IgnoreQueryFilters()
            .Where(p => p.WikiRunId == run.Id)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.PathKey)
            .ToListAsync(ct);
        var nodes = pages
            .GroupBy(p => p.Topic, StringComparer.Ordinal)
            .Select(g => new WikiTreeNode(
                g.Key,
                TopicTitle(g.Key),
                g.Select(p => new WikiPageInfo(
                    p.Id, p.Topic, p.PathKey, p.Title, p.Stale,
                    p.SortOrder)).ToList()))
            .OrderBy(n => TopicOrder(n.Key)).ThenBy(n => n.Key)
            .ToList();
        return new WikiTreeDto(
            repository, run.Branch, run.CommitSha, nodes);
    }

    /// <inheritdoc />
    public async Task<WikiPageDto?> GetPageAsync(
        Guid pageId, CancellationToken ct)
    {
        var p = await db.WikiPages.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == pageId, ct);
        return p is null ? null : ToPageDto(p);
    }

    /// <inheritdoc />
    public async Task<WikiPageDto?> GetPageByTopicAsync(
        string repository, string topic, string? branch, Guid? orgId,
        CancellationToken ct)
    {
        var run = await LatestCompletedRunAsync(
            repository, branch, orgId, ct);
        if (run is null)
        {
            return null;
        }

        var p = await db.WikiPages.IgnoreQueryFilters()
            .Where(x => x.WikiRunId == run.Id
                && (x.PathKey == topic || x.Topic == topic))
            .OrderBy(x => x.SortOrder)
            .FirstOrDefaultAsync(ct);
        return p is null ? null : ToPageDto(p);
    }

    /// <inheritdoc />
    public async Task<WikiRunDto?> GetRunAsync(
        Guid runId, CancellationToken ct)
    {
        var r = await db.WikiRuns.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == runId, ct);
        return r is null ? null : ToDto(r);
    }

    /// <inheritdoc />
    public async Task<bool> HandlePushAsync(
        string repository, string branch,
        IReadOnlyList<string> changedPaths, string? headSha,
        Guid? orgId, CancellationToken ct)
    {
        var touched = false;
        var run = await LatestCompletedRunAsync(
            repository, branch, orgId, ct);
        if (run is not null)
        {
            var changed = new HashSet<string>(
                changedPaths.Select(NormalizePath),
                StringComparer.OrdinalIgnoreCase);
            var pages = await db.WikiPages.IgnoreQueryFilters()
                .Where(p => p.WikiRunId == run.Id && !p.Stale)
                .ToListAsync(ct);
            foreach (var page in pages)
            {
                var sources = SourceFilesOf(page);
                if (sources.Count > 0 && changed.Count > 0
                    && !sources.Any(changed.Contains))
                {
                    continue; // diff-aware: só páginas cobrindo o diff.
                }
                if (changed.Count == 0)
                {
                    continue; // sem paths no payload — não marca nada.
                }
                page.Stale = true;
                page.UpdatedAt = DateTimeOffset.UtcNow;
                touched = true;
            }
            if (touched)
            {
                await db.SaveChangesAsync(ct);
            }

            // Auto re-geração opt-in (wiki.json auto_update persistido no run).
            if (run.AutoUpdate && options.Value.Enabled)
            {
                var next = await GenerateAsync(
                    repository, branch, run.Effort, orgId,
                    requestedByUserId: null, ct);
                if (next is not null)
                {
                    logger.LogInformation(
                        "wiki auto-update enqueued ({Repo}@{Branch}) after push",
                        repository, branch);
                    touched = true;
                }
            }
        }
        return touched;
    }

    /// <summary>Último run completado para (repo, branch, org).</summary>
    internal async Task<WikiRunRecord?> LatestCompletedRunAsync(
        string repository, string? branch, Guid? orgId,
        CancellationToken ct)
    {
        var q = db.WikiRuns.IgnoreQueryFilters()
            .Where(r => r.Repository == repository
                && r.OrgId == orgId
                && r.Status == WikiRunStatuses.Completed);
        if (branch is not null)
        {
            q = q.Where(r => r.Branch == branch);
        }
        var rows = await q.ToListAsync(ct);
        return rows.OrderByDescending(r => r.CreatedAt).FirstOrDefault();
    }

    internal static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    internal static IReadOnlyList<string> SourceFilesOf(WikiPageRecord p) =>
        p.SourceFilesJson is null
            ? []
            : JsonSerializer.Deserialize<List<string>>(p.SourceFilesJson)
                ?.Select(NormalizePath).ToList() ?? [];

    internal static string TopicTitle(string topic) => topic switch
    {
        "overview" => "Overview",
        "architecture" => "Architecture",
        "data-flow" => "Data flow",
        "deployment" => "Deployment",
        "conventions" => "Conventions",
        "modules" => "Modules",
        _ => topic,
    };

    internal static int TopicOrder(string topic) => topic switch
    {
        "overview" => 0,
        "architecture" => 1,
        "modules" => 2,
        "data-flow" => 3,
        "deployment" => 4,
        "conventions" => 5,
        _ => 9,
    };

    internal static WikiRunDto ToDto(WikiRunRecord r) => new(
        r.Id, r.OrgId, r.Repository, r.Branch, r.CommitSha,
        r.Effort, r.Status, r.StatsJson, r.Error,
        r.CreatedAt, r.FinishedAt);

    internal static WikiPageDto ToPageDto(WikiPageRecord p) => new(
        p.Id, p.Repository, p.Branch, p.Topic, p.PathKey,
        p.Title, p.ContentMd, SourceFilesOf(p), p.Stale,
        p.WikiRunId, p.UpdatedAt);
}