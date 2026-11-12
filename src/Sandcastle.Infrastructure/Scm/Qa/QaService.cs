using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Scm;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Scm.Qa;

/// <summary>
/// <see cref="IQaService"/> (SPEC-142 RF-001/RF-004): trigger com dedup
/// <c>(repo, pr, head_sha)</c> + quota diária por repo + enrollment
/// <see cref="ScmConnection.QaCovers"/>, trigger manual, listagem
/// cursada e detalhe (run + plano + passos + links de evidência).
/// </summary>
public sealed class QaService(
    AppDbContext db,
    IQaDispatcher dispatcher,
    IScmProviderRegistry providers,
    IOptions<QaOptions> options,
    ILogger<QaService> logger) : IQaService
{
    /// <inheritdoc />
    public async Task<QaTriggerResult> TriggerAsync(
        ScmConnection connection, string repository, int prNumber,
        string headSha, string? baseSha, string trigger,
        string? requestedByUserId, CancellationToken ct)
    {
        // Dedup (repo, pr, head_sha): run ativo ou já concluído — mesmo
        // head nunca gera segundo run (synchronize com novo head gera).
        var existing = await db.QaRuns.IgnoreQueryFilters()
            .Where(r => r.Repository == repository
                && r.PrNumber == prNumber
                && r.HeadSha == headSha
                && QaRunStatuses.Dedup.Contains(r.Status))
            .OrderByDescending(r => r.CreatedAtTicks)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            logger.LogDebug(
                "qa dedup hit ({Repo}#{Pr} {Sha} → run {Id})",
                repository, prNumber, headSha[..Math.Min(8, headSha.Length)],
                existing.Id);
            return QaTriggerResult.Fail(
                QaTriggerError.Duplicate,
                $"run {existing.Id} already covers this head");
        }

        // Quota diária por repo (SPEC-142 NFR). Comparação por ticks —
        // SQLite não traduz comparação de DateTimeOffset.
        var dayStartTicks = new DateTimeOffset(
            DateTime.UtcNow.Date, TimeSpan.Zero).UtcTicks;
        var runsToday = await db.QaRuns.IgnoreQueryFilters()
            .CountAsync(r => r.Repository == repository
                && r.CreatedAtTicks >= dayStartTicks, ct);
        if (runsToday >= options.Value.MaxDailyRunsPerRepo)
        {
            return QaTriggerResult.Fail(
                QaTriggerError.QuotaExceeded,
                $"daily qa quota reached ({runsToday}/"
                + $"{options.Value.MaxDailyRunsPerRepo})");
        }

        // Link da conversa que originou o PR (evidência herda a ACL).
        var conversationId = await db.Conversations.IgnoreQueryFilters()
            .Where(c => c.SelectedRepository == repository
                && c.PullRequestNumber == prNumber)
            .OrderByDescending(c => c.UpdatedAtTicks)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);

        var run = new QaRunRecord
        {
            Id = Guid.NewGuid(),
            OrgId = connection.OrgId,
            ConnectionId = connection.Id,
            Repository = repository,
            PrNumber = prNumber,
            HeadSha = headSha,
            BaseSha = baseSha,
            Status = QaRunStatuses.Queued,
            ConversationId = conversationId,
            Trigger = trigger,
            RequestedByUserId = requestedByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.QaRuns.Add(run);
        await db.SaveChangesAsync(ct);
        await dispatcher.EnqueueAsync(run.Id, ct);
        logger.LogInformation(
            "qa run {Id} queued ({Repo}#{Pr} {Sha})",
            run.Id, repository, prNumber,
            headSha[..Math.Min(8, headSha.Length)]);
        return QaTriggerResult.Ok(run.Id);
    }

    /// <inheritdoc />
    public async Task<QaTriggerResult> TriggerManualAsync(
        ScmConnection connection, string repository, int prNumber,
        string? requestedByUserId, CancellationToken ct)
    {
        if (!connection.QaCovers(repository))
        {
            return QaTriggerResult.Fail(
                QaTriggerError.Disabled,
                "qa agent not enabled for this repository");
        }
        var provider = providers.Get(connection.Kind);
        if (provider is null)
        {
            return QaTriggerResult.Fail(
                QaTriggerError.NoConnection, "provider not registered");
        }

        ScmPullRequest? pr;
        try
        {
            pr = await provider.GetPullRequestAsync(
                connection, repository, prNumber, ct);
        }
        catch (Exception)
        {
            pr = null;
        }
        if (pr is null || !string.Equals(
                pr.State, "open", StringComparison.OrdinalIgnoreCase))
        {
            return QaTriggerResult.Fail(
                QaTriggerError.NotFound, "pull request not found");
        }

        return await TriggerAsync(connection, repository, prNumber,
            pr.HeadSha, baseSha: null, "manual", requestedByUserId, ct);
    }

    /// <inheritdoc />
    public async Task<QaRunPage> SearchAsync(
        Guid orgId, string? repository, int? prNumber, string? verdict,
        string? cursor, int limit, CancellationToken ct)
    {
        var q = db.QaRuns.IgnoreQueryFilters()
            .Where(r => r.OrgId == orgId);
        if (!string.IsNullOrEmpty(repository))
        {
            q = q.Where(r => r.Repository == repository);
        }
        if (prNumber is { } pr)
        {
            q = q.Where(r => r.PrNumber == pr);
        }
        if (!string.IsNullOrEmpty(verdict))
        {
            q = q.Where(r => r.Verdict == verdict);
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
        var counts = await StepCountsAsync(
            rows.Select(r => r.Id).ToList(), ct);
        return new QaRunPage(
            rows.Select(r => ToModel(r,
                counts.GetValueOrDefault(r.Id))).ToList(), next);
    }

    /// <inheritdoc />
    public async Task<QaRunDetail?> GetAsync(
        Guid orgId, Guid id, CancellationToken ct)
    {
        var run = await db.QaRuns.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == id && r.OrgId == orgId, ct);
        if (run is null)
        {
            return null;
        }
        var steps = await db.QaSteps.IgnoreQueryFilters()
            .Where(s => s.QaRunId == id)
            .ToListAsync(ct);
        steps.Sort((a, b) => a.Number.CompareTo(b.Number));

        var basePath = $"/api/v1/qa/runs/{id}/artifacts";
        var evidence = new List<QaEvidenceLink>();
        foreach (var s in steps.Where(s => s.ArtifactRef is not null))
        {
            var isVideo = s.Action == "video";
            var name = isVideo
                ? $"session-{s.Number}.webm"
                : $"step-{s.Number}.png";
            evidence.Add(new QaEvidenceLink(
                isVideo
                    ? QaDefaults.ArtifactKindVideo
                    : QaDefaults.ArtifactKindScreenshot,
                name,
                $"{basePath}/{Uri.EscapeDataString(s.ArtifactRef!)}"));
        }

        return new QaRunDetail(
            ToModel(run, steps.Count), ParsePlan(run.PlanJson),
            steps.Select(ToModel).ToList(), evidence);
    }

    /// <summary>Contagem de passos por run (uma query agrupada).</summary>
    private async Task<Dictionary<Guid, int>> StepCountsAsync(
        IReadOnlyList<Guid> runIds, CancellationToken ct) =>
        await db.QaSteps.IgnoreQueryFilters()
            .Where(s => runIds.Contains(s.QaRunId))
            .GroupBy(s => s.QaRunId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

    /// <summary>Reidrata o plano persistido — null quando ausente/corrompido.</summary>
    internal static QaPlan? ParsePlan(string? planJson)
    {
        if (string.IsNullOrEmpty(planJson))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<QaPlan>(planJson,
                QaPlanSerialization.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static QaRun ToModel(QaRunRecord r, int stepCount) => new(
        r.Id, r.OrgId, r.ConnectionId, r.Repository, r.PrNumber,
        r.HeadSha, r.BaseSha, r.Status, r.Verdict, r.Classification,
        r.PartialPlan, r.ConversationId, r.Trigger, r.RequestedByUserId,
        r.Error, stepCount, r.CreatedAt, r.FinishedAt);

    private static QaStep ToModel(QaStepRecord s) => new(
        s.Id, s.QaRunId, s.Number, s.Action, s.Target, s.Outcome,
        s.Detail, s.ArtifactRef, s.DurationMs, s.CreatedAt);
}

/// <summary>Serializer options compartilhadas do plano QA (snake_case).</summary>
public static class QaPlanSerialization
{
    /// <summary>snake_case + ignore null — mesmo contrato do LLM.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}