using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Scm;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Scm.Review;

/// <summary>
/// <see cref="IReviewService"/> (SPEC-140 RF-001/RF-004/RF-005): trigger
/// com dedup <c>(repo, pr, head_sha)</c> + quota diária por repo, listagem
/// cursada, detalhe e PATCH de finding.
/// </summary>
public sealed class ReviewService(
    AppDbContext db,
    IReviewDispatcher dispatcher,
    IOptions<ReviewOptions> options,
    ILogger<ReviewService> logger) : IReviewService
{
    private static readonly string[] ActiveStatuses =
    [
        ReviewRunStatuses.Queued, ReviewRunStatuses.Running,
        ReviewRunStatuses.Completed, ReviewRunStatuses.Skipped,
    ];

    /// <inheritdoc />
    public async Task<ReviewTriggerResult> TriggerAsync(
        ScmConnection connection, string repository, int prNumber,
        string headSha, string trigger, string? requestedByUserId,
        CancellationToken ct)
    {
        // Dedup (repo, pr, head_sha): run ativo ou já concluído — mesmo
        // head nunca gera segundo run (synchronize com novo head gera).
        var existing = await db.ReviewRuns.IgnoreQueryFilters()
            .Where(r => r.Repository == repository
                && r.PrNumber == prNumber
                && r.HeadSha == headSha
                && ActiveStatuses.Contains(r.Status))
            .OrderByDescending(r => r.CreatedAtTicks)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            logger.LogDebug(
                "review dedup hit ({Repo}#{Pr} {Sha} → run {Id})",
                repository, prNumber, headSha[..Math.Min(8, headSha.Length)],
                existing.Id);
            return ReviewTriggerResult.Fail(
                ReviewTriggerError.Duplicate,
                $"run {existing.Id} already covers this head");
        }

        // Quota diária por repo (SPEC-140 RF-005). Comparação por ticks —
        // SQLite não traduz comparação de DateTimeOffset.
        var dayStartTicks = new DateTimeOffset(
            DateTime.UtcNow.Date, TimeSpan.Zero).UtcTicks;
        var runsToday = await db.ReviewRuns.IgnoreQueryFilters()
            .CountAsync(r => r.Repository == repository
                && r.CreatedAtTicks >= dayStartTicks, ct);
        if (runsToday >= options.Value.MaxDailyRunsPerRepo)
        {
            return ReviewTriggerResult.Fail(
                ReviewTriggerError.QuotaExceeded,
                $"daily review quota reached ({runsToday}/"
                + $"{options.Value.MaxDailyRunsPerRepo})");
        }

        var run = new ReviewRunRecord
        {
            Id = Guid.NewGuid(),
            OrgId = connection.OrgId,
            ConnectionId = connection.Id,
            Repository = repository,
            PrNumber = prNumber,
            HeadSha = headSha,
            Status = ReviewRunStatuses.Queued,
            Trigger = trigger,
            RequestedByUserId = requestedByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ReviewRuns.Add(run);
        await db.SaveChangesAsync(ct);
        await dispatcher.EnqueueAsync(run.Id, ct);
        return ReviewTriggerResult.Ok(run.Id);
    }

    /// <inheritdoc />
    public async Task<ReviewRunPage> SearchAsync(
        Guid orgId, string? repository, int? prNumber, string? status,
        string? cursor, int limit, CancellationToken ct)
    {
        var q = db.ReviewRuns.IgnoreQueryFilters()
            .Where(r => r.OrgId == orgId);
        if (!string.IsNullOrEmpty(repository))
        {
            q = q.Where(r => r.Repository == repository);
        }
        if (prNumber is { } pr)
        {
            q = q.Where(r => r.PrNumber == pr);
        }
        if (!string.IsNullOrEmpty(status))
        {
            q = q.Where(r => r.Status == status);
        }
        if (CursorPaginator.TryDecode(cursor, out var ticks, out var raw)
            && Guid.TryParse(raw, out _))
        {
            // Sqlite persiste Guid como TEXT uppercase; normalizar os dois
            // lados via LOWER() mantém o tie-break consistente com o
            // ORDER BY id (#424).
            var id = raw.ToLowerInvariant();
            q = q.Where(r => r.CreatedAtTicks < ticks
                || (r.CreatedAtTicks == ticks
                    && string.Compare(r.Id.ToString().ToLower(), id) < 0));
        }

        var pageSize = Math.Clamp(limit, 1, 100);
        var rows = await q
            .OrderByDescending(r => r.CreatedAtTicks)
            .ThenByDescending(r => r.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        string? next = null;
        if (rows.Count > pageSize)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            next = CursorPaginator.Encode(
                last.CreatedAtTicks, last.Id.ToString());
        }
        var counts = await FindingCountsAsync(
            rows.Select(r => r.Id).ToList(), ct);
        return new ReviewRunPage(
            rows.Select(r => ToModel(r,
                counts.GetValueOrDefault(r.Id))).ToList(), next);
    }

    /// <inheritdoc />
    public async Task<ReviewRunDetail?> GetAsync(
        Guid orgId, Guid id, CancellationToken ct)
    {
        var run = await db.ReviewRuns.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == id && r.OrgId == orgId, ct);
        if (run is null)
        {
            return null;
        }
        // Ordem em memória — SQLite não suporta ORDER BY de DateTimeOffset
        // (volume pequeno: findings de um run cabem no cap inline ×2).
        var findingRows = await db.ReviewFindings.IgnoreQueryFilters()
            .Where(f => f.ReviewRunId == id)
            .ToListAsync(ct);
        findingRows.Sort(
            (a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        return new ReviewRunDetail(
            ToModel(run, findingRows.Count),
            findingRows.Select(ToModel).ToList());
    }

    /// <summary>Contagem de findings por run (uma query agrupada).</summary>
    private async Task<Dictionary<Guid, int>> FindingCountsAsync(
        IReadOnlyList<Guid> runIds, CancellationToken ct) =>
        await db.ReviewFindings.IgnoreQueryFilters()
            .Where(f => runIds.Contains(f.ReviewRunId))
            .GroupBy(f => f.ReviewRunId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

    /// <inheritdoc />
    public async Task<ReviewFinding?> UpdateFindingStatusAsync(
        Guid orgId, Guid runId, Guid findingId, string status,
        CancellationToken ct)
    {
        if (!ReviewFindingStatuses.Updatable.Contains(status))
        {
            return null;
        }
        var finding = await db.ReviewFindings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == findingId
                && f.ReviewRunId == runId && f.OrgId == orgId, ct);
        if (finding is null)
        {
            return null;
        }
        finding.Status = status;
        finding.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToModel(finding);
    }

    private static ReviewRun ToModel(ReviewRunRecord r, int findingCount) => new(
        r.Id, r.OrgId, r.ConnectionId, r.Repository, r.PrNumber,
        r.HeadSha, r.DiffHash, r.Status, r.Partial, r.Trigger,
        r.RequestedByUserId, r.Error, r.SummaryText, findingCount,
        r.CreatedAt, r.FinishedAt);

    private static ReviewFinding ToModel(ReviewFindingRecord f) => new(
        f.Id, f.ReviewRunId, f.Repository, f.PrNumber, f.Kind,
        f.Severity, f.Confidence, f.Path, f.Line, f.Title, f.Body,
        f.SuggestedPatch, f.Status, f.CommentUrl, f.Permalink,
        f.CreatedAt);
}