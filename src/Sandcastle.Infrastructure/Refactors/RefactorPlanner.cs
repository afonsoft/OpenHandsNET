using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Indexing;
using Sandcastle.Application.Refactors;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Refactors;

/// <summary>
/// <see cref="IRefactorPlanner"/> — SPEC-144 RF-001. Planning run per
/// plan: acquires each repo's content (<see cref="IRepoContentProvider"/>,
/// shallow clone inside the lease), enumerates files, builds the
/// dependency graph (<see cref="RefactorRepoAnalyzer"/> — C#
/// <c>using</c>/project refs first; TS/JS + Python heuristic; directory
/// frontier when the graph is empty or strategy=directory), topologically
/// batches via <see cref="RefactorBatchPlanner"/>, persists
/// <c>refactor_plan_v1</c> + batch rows and moves the plan to
/// <c>awaiting_approval</c> (or straight to execution when the approval
/// gate is disabled).
/// </summary>
public sealed class RefactorPlanner(
    AppDbContext db,
    IRepoContentProvider content,
    IRefactorDispatcher dispatcher,
    IOptions<RefactorOptions> options,
    ILogger<RefactorPlanner> logger,
    Sandcastle.Application.Identity.IAuditService? audit = null)
    : IRefactorPlanner
{
    /// <summary>Current <c>plan_json</c> schema marker.</summary>
    public const string PlanSchema = "refactor_plan_v1";

    /// <inheritdoc />
    public async Task RunAsync(Guid planId, CancellationToken ct)
    {
        var plan = await db.RefactorPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan is null
            || plan.Status != RefactorPlanStatuses.Planning)
        {
            return;
        }

        var o = options.Value;
        var stats = new Dictionary<string, object?>
        {
            ["repos"] = 0,
            ["repos_failed"] = 0,
            ["files"] = 0,
            ["edges"] = 0,
            ["batches"] = 0,
            ["errors"] = new List<string>(),
        };
        var errors = (List<string>)stats["errors"]!;

        try
        {
            var repos = RefactorService.DeserializeRepos(
                plan.RepositoriesJson);
            if (repos.Count == 0)
            {
                await FailAsync(plan, "no repositories", stats, ct);
                return;
            }

            var warnings = new List<string>();
            var allBatches = new List<RefactorBatchInput>();
            var batchSize = Math.Max(1,
                plan.BatchSizeHint ?? o.DefaultBatchSize);
            var n = 1;
            var effectiveMode = plan.Mode;

            foreach (var repo in repos)
            {
                try
                {
                    var (batches, mode, repoWarnings) =
                        await PlanRepoAsync(repo, plan, batchSize, n, o,
                            ct);
                    n += batches.Count;
                    allBatches.AddRange(batches);
                    warnings.AddRange(repoWarnings);
                    stats["repos"] = (int)stats["repos"]! + 1;
                    stats["files"] = (int)stats["files"]!
                        + batches.Sum(b => b.Files.Count);
                    if (mode == RefactorModes.Stacked)
                    {
                        effectiveMode = RefactorModes.Stacked;
                    }
                }
                catch (Exception e)
                    when (e is not OperationCanceledException)
                {
                    stats["repos_failed"] =
                        (int)stats["repos_failed"]! + 1;
                    errors.Add($"{repo}: {e.Message}");
                    logger.LogWarning(e,
                        "refactor plan {Id}: repo {Repo} failed",
                        planId, repo);
                }
            }

            if (allBatches.Count == 0)
            {
                await FailAsync(plan,
                    "no repository produced a plan (empty or "
                    + "unreachable repos)", stats, ct);
                return;
            }

            // Persist plan_json + batch rows.
            var now = DateTimeOffset.UtcNow;
            plan.PlanJson = JsonSerializer.Serialize(new
            {
                schema = PlanSchema,
                goal = plan.Goal,
                strategy = plan.Strategy,
                mode = effectiveMode,
                batches = allBatches.Select((b, i) => new
                {
                    n = i + 1,
                    repository = b.Repository,
                    files = b.Files,
                    depends_on = b.DependsOn,
                    description = b.Description,
                    verify_cmd = b.VerifyCommand,
                    risk = b.Risk,
                }),
                warnings,
            });
            plan.Mode = effectiveMode;
            plan.Status = RefactorPlanStatuses.AwaitingApproval;
            plan.StatsJson = JsonSerializer.Serialize(stats);

            for (var i = 0; i < allBatches.Count; i++)
            {
                var b = allBatches[i];
                db.RefactorBatches.Add(new RefactorBatchRecord
                {
                    Id = Guid.NewGuid(),
                    PlanId = plan.Id,
                    N = i + 1,
                    Repository = b.Repository,
                    FilesJson = JsonSerializer.Serialize(b.Files),
                    DependsOnJson = JsonSerializer.Serialize(b.DependsOn),
                    Description = b.Description is { Length: > 500 } d
                        ? d[..500] : b.Description,
                    Risk = b.Risk,
                    VerifyCommand = b.VerifyCommand,
                    State = RefactorBatchStates.Pending,
                    Attempts = 0,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            stats["batches"] = allBatches.Count;
            await db.SaveChangesAsync(ct);
            await AuditAsync("refactor.plan_ready", plan, new
            {
                batches = allBatches.Count,
                mode = effectiveMode,
                warnings,
            }, ct);

            // Gate off → skip the human gate (RequirePlanApproval=false).
            if (!o.RequirePlanApproval)
            {
                plan.Status = RefactorPlanStatuses.Running;
                plan.StartedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                await dispatcher.EnqueueAsync(
                    plan.Id, RefactorWorkKind.Execute, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "refactor plan {Id} planning failed", planId);
            try
            {
                await FailAsync(plan, e.Message, stats,
                    CancellationToken.None);
            }
            catch (Exception inner)
            {
                logger.LogWarning(inner,
                    "refactor plan {Id} failed-status write failed", planId);
            }
        }
    }

    /// <summary>
    /// Plans one repo inside a content lease: enumerate → import graph
    /// (strategy) or directory frontier → topo batches numbered from
    /// <paramref name="startN"/>.
    /// </summary>
    private async Task<(IReadOnlyList<RefactorBatchInput> Batches,
        string Mode, IReadOnlyList<string> Warnings)> PlanRepoAsync(
        string repo, RefactorPlanRecord plan, int batchSize, int startN,
        RefactorOptions o, CancellationToken ct)
    {
        await using var lease = await content.AcquireAsync(
            new RepoContentRequest(repo, "", plan.OrgId), ct);
        if (lease is null)
        {
            throw new InvalidOperationException("no content provider");
        }

        var files = RefactorRepoAnalyzer.EnumerateFiles(lease.RootPath);
        if (files.Count == 0)
        {
            throw new InvalidOperationException("no indexable files");
        }

        IReadOnlyDictionary<string, IReadOnlyCollection<string>> edges =
            new Dictionary<string, IReadOnlyCollection<string>>();
        var warnings = new List<string>();
        if (plan.Strategy == RefactorStrategies.ImportGraph)
        {
            edges = RefactorRepoAnalyzer.BuildImportGraph(
                lease.RootPath, files);
            if (edges.Count == 0)
            {
                warnings.Add(
                    $"{repo}: empty import graph — directory frontier "
                    + "batching applied");
            }
        }

        // Directory strategy or empty graph → directory frontiers
        // (no edges: batches independent).
        var verify = RefactorRepoAnalyzer.InferVerifyCommand(
            lease.RootPath);
        var output = RefactorBatchPlanner.Plan(
            repo, files, edges, plan.Mode, batchSize,
            o.MaxBatchFiles, o.MaxBatches, verify, startN);
        warnings.AddRange(output.Warnings);
        return (output.Batches, output.Mode, warnings);
    }

    private async Task FailAsync(RefactorPlanRecord plan, string error,
        Dictionary<string, object?> stats, CancellationToken ct)
    {
        plan.Status = RefactorPlanStatuses.Failed;
        plan.Error = error.Length > 1900 ? error[..1900] : error;
        plan.FinishedAt = DateTimeOffset.UtcNow;
        plan.StatsJson = JsonSerializer.Serialize(stats);
        await db.SaveChangesAsync(ct);
        await AuditAsync("refactor.plan_failed", plan, new
        {
            error = plan.Error,
            stats,
        }, ct);
    }

    private async Task AuditAsync(string action, RefactorPlanRecord plan,
        object? metadata, CancellationToken ct)
    {
        if (audit is null)
        {
            return;
        }

        try
        {
            await audit.RecordAsync(action,
                actorUserId: null, orgId: plan.OrgId,
                targetType: "refactor_plan",
                targetId: plan.Id.ToString(),
                metadata: metadata, cancellationToken: ct);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "refactor audit {Action} skipped", action);
        }
    }
}