using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Eval;
using Sandcastle.Application.Identity;

namespace Sandcastle.Infrastructure.Eval;

/// <summary>
/// SPEC-152 RF-001/RF-004 — eval run lifecycle: create (dedup + cost
/// gates + estimate gate), list/get/results, cancel (runner polls),
/// estimate (avg tokens × n × model pricing) and the scorecard
/// (resolved% por model/route sobre as últimas N runs).
/// </summary>
public sealed class EvalService(
    Persistence.AppDbContext db,
    IEvalDispatcher dispatcher,
    IEvalSuiteResolver suites,
    IOptions<EvalOptions> options,
    IModelCatalog? models = null,
    IAuditService? audit = null,
    ILogger<EvalService>? logger = null) : IEvalService
{
    private readonly EvalOptions _opt = options.Value;

    /// <inheritdoc />
    public async Task<(EvalRunCreated? Created, string? Error,
        EvalEstimate? Gate)> CreateAsync(
            CreateEvalRunRequest request, Guid ownerUserId, Guid? orgId,
            CancellationToken ct)
    {
        if (!EvalSuites.IsValid(request.Suite))
        {
            return (null, "invalid_suite", null);
        }

        if (request.N is <= 0)
        {
            return (null, "invalid_n", null);
        }

        if (request.Suite == EvalSuites.Custom
            && string.IsNullOrWhiteSpace(request.TasksPath))
        {
            return (null, "tasks_path_required", null);
        }

        var adapter = suites.Get(request.Suite)!;
        IReadOnlyList<EvalTaskV1> tasks;
        try
        {
            tasks = await adapter.LoadAsync(new EvalSuiteLoadContext(
                request.TasksPath, _opt.DatasetCacheDir), ct);
        }
        catch (EvalSuiteException ex)
        {
            return (null, $"suite_load: {ex.Message}", null);
        }

        var effectiveN = Math.Min(
            request.N ?? tasks.Count, tasks.Count);
        if (effectiveN > _opt.MaxTasks)
        {
            return (null, $"max_tasks_exceeded:{_opt.MaxTasks}", null);
        }

        // Dedup: mesma chave (suite+model+route+tasks_path+n) com run
        // ativa → retorna a existente (idempotente, 202).
        var active = new[] { EvalRunStatuses.Queued,
            EvalRunStatuses.Running };
        var existing = await db.EvalRuns.AsNoTracking()
            .Where(r => r.Suite == request.Suite
                && r.Model == request.Model
                && r.RouteOverride == request.RouteOverride
                && r.DatasetPath == request.TasksPath
                && r.NTasks == effectiveN
                && active.Contains(r.Status))
            .OrderByDescending(r => r.CreatedAtTicks)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return (new EvalRunCreated(existing.Id, existing.Status,
                existing.NTasks, existing.EstimatedCostUsd), null, null);
        }

        var estimate = await BuildEstimateAsync(
            request.Suite, effectiveN, request.Model, orgId, ct);
        if (effectiveN > _opt.EstimateGateTasks
            && !request.EstimateAcknowledged)
        {
            return (null, "estimate_required",
                estimate with { RequiresAcknowledgement = true });
        }

        // RF-004: cap de gasto diário por org (soma eval_results do
        // dia UTC + estimativa desta run).
        if (_opt.MaxDailyCostUsd is { } cap && cap > 0
            && orgId is not null)
        {
            // Comparação via CreatedAtTicks — DateTimeOffset não
            // ordena/compara no SQLite (convenção do repo).
            var dayStartTicks = new DateTimeOffset(
                DateTime.UtcNow.Date, TimeSpan.Zero).UtcTicks;
            var spentToday = await db.EvalResults.AsNoTracking()
                .Where(r => r.OrgId == orgId
                    && r.CreatedAtTicks >= dayStartTicks)
                .SumAsync(r => (decimal?)r.CostUsd, ct) ?? 0m;
            if (spentToday + (estimate.EstimatedCostUsd ?? 0m) > cap)
            {
                return (null,
                    $"daily_cost_exceeded:{cap:0.##}", null);
            }
        }

        var run = new EvalRunRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            CreatedByUserId = ownerUserId.ToString(),
            Suite = request.Suite,
            NTasks = effectiveN,
            Model = request.Model,
            RouteOverride = request.RouteOverride,
            Status = EvalRunStatuses.Queued,
            DatasetPath = request.TasksPath,
            EstimatedCostUsd = estimate.EstimatedCostUsd,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.EvalRuns.Add(run);
        await db.SaveChangesAsync(ct);
        await dispatcher.EnqueueAsync(run.Id, ct);
        await AuditAsync("eval_run.created", run.Id,
            ownerUserId, orgId, new
            {
                suite = run.Suite,
                n_tasks = run.NTasks,
                model = run.Model,
                route = run.RouteOverride,
                estimated_cost_usd = run.EstimatedCostUsd,
            }, ct);
        logger?.LogInformation(
            "eval run {RunId} queued (suite={Suite} n={N} model={Model})",
            run.Id, run.Suite, run.NTasks, run.Model);
        return (new EvalRunCreated(run.Id, run.Status, run.NTasks,
            run.EstimatedCostUsd), null, null);
    }

    /// <inheritdoc />
    public async Task<EvalRunSummary?> GetAsync(
        Guid orgId, Guid runId, CancellationToken ct)
    {
        var r = await db.EvalRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == runId, ct);
        return r is null || r.OrgId != orgId ? null : ToSummary(r);
    }

    /// <inheritdoc />
    public async Task<EvalRunPage> ListAsync(Guid orgId, string? suite,
        string? status, string? cursor, int limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit, 1, 100);
        var q = db.EvalRuns.AsNoTracking()
            .Where(r => r.OrgId == orgId);
        if (suite is { Length: > 0 })
        {
            q = q.Where(r => r.Suite == suite);
        }

        if (status is { Length: > 0 })
        {
            q = q.Where(r => r.Status == status);
        }

        if (cursor is { Length: > 0 }
            && long.TryParse(cursor, out var ticks))
        {
            q = q.Where(r => r.CreatedAtTicks < ticks);
        }

        var items = await q.OrderByDescending(r => r.CreatedAtTicks)
            .Take(take + 1).ToListAsync(ct);
        string? next = null;
        if (items.Count > take)
        {
            items.RemoveAt(items.Count - 1);
            next = items[^1].CreatedAtTicks.ToString();
        }

        return new EvalRunPage(
            items.Select(ToSummary).ToList(), next);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvalResultInfo>?> ListResultsAsync(
        Guid orgId, Guid runId, CancellationToken ct)
    {
        var run = await db.EvalRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.OrgId != orgId)
        {
            return null;
        }

        var rows = await db.EvalResults.AsNoTracking()
            .Where(r => r.RunId == runId)
            .OrderBy(r => r.CreatedAtTicks)
            .ToListAsync(ct);
        return rows.Select(ToInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<string?> CancelAsync(
        Guid orgId, Guid runId, CancellationToken ct)
    {
        var run = await db.EvalRuns
            .FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.OrgId != orgId)
        {
            return "not_found";
        }

        if (EvalRunStatuses.Terminal.Contains(run.Status))
        {
            return "terminal";
        }

        run.Status = EvalRunStatuses.Cancelled;
        run.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await AuditAsync("eval_run.cancelled", run.Id,
            null, orgId, new { suite = run.Suite }, ct);
        return null;
    }

    /// <inheritdoc />
    public async Task<EvalEstimate?> EstimateAsync(
        EvalEstimateRequest request, Guid? orgId, CancellationToken ct)
    {
        if (!EvalSuites.IsValid(request.Suite))
        {
            return null;
        }

        var n = request.N ?? 0;
        if (n <= 0)
        {
            try
            {
                var adapter = suites.Get(request.Suite)!;
                n = (await adapter.LoadAsync(new EvalSuiteLoadContext(
                    request.TasksPath, _opt.DatasetCacheDir), ct)).Count;
            }
            catch (EvalSuiteException)
            {
                return null;
            }
        }

        return await BuildEstimateAsync(
            request.Suite, n, request.Model, orgId, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvalScorecardRow>> ScorecardAsync(
        Guid? orgId, string? suite, int last, CancellationToken ct)
    {
        var q = db.EvalRuns.AsNoTracking()
            .Where(r => r.Status == EvalRunStatuses.Completed);
        if (orgId is not null)
        {
            q = q.Where(r => r.OrgId == orgId);
        }

        if (suite is { Length: > 0 })
        {
            q = q.Where(r => r.Suite == suite);
        }

        var runs = await q.OrderByDescending(r => r.CreatedAtTicks)
            .Take(Math.Clamp(last, 1, 200)).ToListAsync(ct);
        return runs
            .GroupBy(r => (r.Model, r.RouteOverride))
            .Select(g => new EvalScorecardRow(
                g.Key.Model,
                g.Key.RouteOverride,
                g.Count(),
                g.Sum(r => r.NTasks),
                g.Sum(r => r.ResolvedN),
                g.Sum(r => r.NTasks) == 0 ? 0
                    : Math.Round(100.0 * g.Sum(r => r.ResolvedN)
                        / g.Sum(r => r.NTasks), 1),
                0L,
                g.Sum(r => r.ActualCostUsd ?? 0m),
                g.Max(r => r.FinishedAt)))
            .OrderByDescending(r => r.ResolvedPct)
            .ToList();
    }

    /// <summary>Cost estimate: avg tokens/task × n × model pricing.</summary>
    internal async Task<EvalEstimate> BuildEstimateAsync(
        string suite, int n, string? model, Guid? orgId,
        CancellationToken ct)
    {
        // Média observada na org (últimas runs) — fallback p/ defaults.
        var (avgPrompt, avgCompletion) = await ObservedAveragesAsync(
            orgId, ct);
        var pricingKnown = false;
        if (model is { Length: > 0 } && models is not null)
        {
            var info = await models.GetAsync(model, ct);
            if (info?.Pricing?.InputPerMillion is { } i
                && info.Pricing.OutputPerMillion is { } o)
            {
                pricingKnown = true;
                var cost = n * (avgPrompt * i / 1_000_000m
                    + avgCompletion * o / 1_000_000m);
                return new EvalEstimate(suite, n, model,
                    avgPrompt, avgCompletion,
                    Math.Round(cost, 4), true,
                    n > _opt.EstimateGateTasks, null);
            }
        }

        return new EvalEstimate(suite, n, model, avgPrompt,
            avgCompletion, null, pricingKnown,
            n > _opt.EstimateGateTasks,
            model is null
                ? "model unset — router will pick at run time"
                : "model pricing unknown — cost shown after run");
    }

    /// <summary>Observed avg tokens per task (org history; 0 → defaults).</summary>
    private async Task<(long Prompt, long Completion)>
        ObservedAveragesAsync(Guid? orgId, CancellationToken ct)
    {
        var q = db.EvalResults.AsNoTracking()
            .Where(r => r.Verdict != null && r.Tokens > 0);
        if (orgId is not null)
        {
            q = q.Where(r => r.OrgId == orgId);
        }

        var stats = await q
            .GroupBy(_ => 1)
            .Select(g => new
            {
                P = g.Average(r => (double)r.PromptTokens),
                C = g.Average(r => (double)r.CompletionTokens),
            }).FirstOrDefaultAsync(ct);
        return stats is null
            ? (_opt.EstimatedPromptTokensPerTask,
                _opt.EstimatedCompletionTokensPerTask)
            : ((long)Math.Round(stats.P),
                (long)Math.Round(stats.C));
    }

    internal static EvalRunSummary ToSummary(EvalRunRecord r) => new(
        r.Id, r.OrgId, r.Suite, r.NTasks, r.Model, r.RouteOverride,
        r.Status, r.ResolvedN, r.FailedN, r.ErrorN, r.TimeoutN,
        r.ScorePct, r.EstimatedCostUsd, r.ActualCostUsd, r.Error,
        r.CreatedAt, r.FinishedAt);

    internal static EvalResultInfo ToInfo(EvalResultRecord r) => new(
        r.Id, r.RunId, r.TaskId, r.Status, r.Verdict, r.SessionId,
        r.DurationS, r.Tokens, r.CostUsd, r.LogDigest, r.Error,
        r.FinishedAt);

    private async Task AuditAsync(string action, Guid runId,
        Guid? actor, Guid? orgId, object? metadata, CancellationToken ct)
    {
        if (audit is null)
        {
            return;
        }

        try
        {
            await audit.RecordAsync(action,
                actorUserId: actor?.ToString(), orgId: orgId,
                targetType: "eval_run", targetId: runId.ToString(),
                metadata: metadata, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "eval audit {Action} failed", action);
        }
    }

    /// <summary>
    /// Aggregated stats persisted in <c>eval_runs.stats_json</c> when the
    /// run lands on a terminal state (runner path).
    /// </summary>
    internal static string BuildStatsJson(EvalRunRecord run,
        IReadOnlyList<EvalResultRecord> results) =>
        JsonSerializer.Serialize(new
        {
            wall_seconds = run.StartedAt is { } s && run.FinishedAt is { } f
                ? Math.Round((f - s).TotalSeconds, 1) : (double?)null,
            tasks = run.NTasks,
            verdicts = results
                .Where(r => r.Verdict is not null)
                .GroupBy(r => r.Verdict!)
                .ToDictionary(g => g.Key, g => g.Count()),
            prompt_tokens = results.Sum(r => r.PromptTokens),
            completion_tokens = results.Sum(r => r.CompletionTokens),
            total_tokens = results.Sum(r => r.Tokens),
            cost_usd = results.Sum(r => r.CostUsd),
        });
}