using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Events;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Refactors;
using Sandcastle.Application.Scm;
using Sandcastle.Domain.Events;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Refactors;

/// <summary>
/// <see cref="IRefactorService"/> — SPEC-144. Creation builds the
/// planner conversation shell (agent_type <c>refactor-planner</c>,
/// Run:false — the deterministic planner job does the graph work) and
/// enqueues planning. Start is the approval gate: creates the
/// coordinator shell (agent_type <c>refactor-coordinator</c>) that
/// parents every batch child, then enqueues execution. Reads are
/// org-scoped via <c>IgnoreQueryFilters</c> + explicit predicates (the
/// global filter is user-context-bound; services run outside it).
/// </summary>
public sealed class RefactorService(
    AppDbContext db,
    IAppConversationService conversations,
    IRefactorDispatcher dispatcher,
    IOptions<RefactorOptions> options,
    ILogger<RefactorService> logger,
    IEventService? events = null,
    IAgentRunnerHost? runnerHost = null,
    IScmConnectionService? connections = null,
    IScmProviderRegistry? providers = null,
    IAuditService? audit = null)
    : IRefactorService
{
    /// <inheritdoc />
    public async Task<(RefactorCreated? Created, string? Error)> CreateAsync(
        CreateRefactorRequest request, Guid ownerUserId, Guid? orgId,
        CancellationToken ct)
    {
        // --- validation (400 surface) ---
        var goal = request.Goal?.Trim();
        if (string.IsNullOrEmpty(goal))
        {
            return (null, "goal is required");
        }
        if (goal.Length > 4000)
        {
            return (null, "goal too long (max 4000 chars)");
        }

        var o = options.Value;
        var strategy = RefactorStrategies.Normalize(request.Strategy);
        if (strategy is null)
        {
            return (null,
                $"invalid strategy '{request.Strategy}' "
                + "(directory|import-graph)");
        }
        var mode = RefactorModes.Normalize(request.Mode);
        if (mode is null)
        {
            return (null,
                $"invalid mode '{request.Mode}' (independent|stacked)");
        }
        if (mode == RefactorModes.Stacked)
        {
            // GitHub-only (SPEC-144 RF-002): stacked PR chains need the
            // base=head-of-previous contract — reject early when the org
            // has no GitHub connection covering every requested repo.
            if (orgId is null)
            {
                return (null,
                    "mode=stacked requires GitHub scm connections");
            }
        }

        IReadOnlyList<string> repos;
        try
        {
            repos = RepositorySelection.Normalize(
                null, request.Repositories, o.MaxRepos);
        }
        catch (RepositorySelectionException e)
        {
            return (null, e.Message);
        }
        if (repos.Count == 0)
        {
            return (null, "at least one repository is required");
        }

        if (mode == RefactorModes.Stacked && repos.Count > 1)
        {
            return (null,
                "mode=stacked supports a single repository "
                + "(cross-repo stacks are out of scope)");
        }

        var batchSize = request.BatchSizeHint;
        if (batchSize is <= 0)
        {
            return (null, "batch_size_hint must be positive");
        }

        // --- planner conversation shell (Run:false — o job
        // determinístico constrói o grafo; a conversa ancora o plano
        // e carrega o agent_type refactor-planner p/ UI/auditoria).
        var plannerTask = await conversations.CreateForUserAsync(
            ownerUserId, orgId,
            new CreateConversationRequest(
                InitialMessage: new InitialMessage(
                    "user",
                    [$"Batch refactor ({strategy}, {mode}): {goal}"],
                    Run: false),
                SelectedRepository: repos[0],
                AgentType: RefactorAgentTypes.Planner,
                LlmModel: null,
                SandboxSpecId: null,
                SelectedRepositories: repos),
            ct);

        var now = DateTimeOffset.UtcNow;
        var plan = new RefactorPlanRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            CreatedByUserId = ownerUserId.ToString(),
            RepositoriesJson = JsonSerializer.Serialize(repos),
            Goal = goal,
            Strategy = strategy,
            Mode = mode,
            RequestedMode = mode,
            MaxConcurrency = o.EffectiveConcurrency(
                request.MaxConcurrency),
            BatchSizeHint = batchSize,
            Status = RefactorPlanStatuses.Planning,
            PlannerConversationId = plannerTask.AppConversationId,
            CreatedAt = now,
        };
        db.RefactorPlans.Add(plan);
        await db.SaveChangesAsync(ct);

        await AuditAsync("refactor.plan_created", ownerUserId, orgId,
            plan.Id, new
            {
                strategy,
                mode,
                repos = repos.Count,
                max_concurrency = plan.MaxConcurrency,
                planner = plannerTask.AppConversationId,
            }, ct);

        await dispatcher.EnqueueAsync(
            plan.Id, RefactorWorkKind.Plan, ct);
        return (new RefactorCreated(
            plan.Id,
            plannerTask.AppConversationId ?? Guid.Empty,
            plan.Status), null);
    }

    /// <inheritdoc />
    public async Task<RefactorPlanSummary?> GetAsync(
        Guid orgId, Guid planId, CancellationToken ct)
    {
        var plan = await db.RefactorPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                p => p.Id == planId && p.OrgId == orgId, ct);
        if (plan is null)
        {
            return null;
        }

        var counts = await StateCountsAsync([planId], ct);
        var total = counts.GetValueOrDefault(planId);
        return ToSummary(plan, total.Total, total.ByState);
    }

    /// <inheritdoc />
    public async Task<RefactorPlanPage> ListAsync(
        Guid orgId, string? status, string? cursor, int limit,
        CancellationToken ct)
    {
        var q = db.RefactorPlans.IgnoreQueryFilters()
            .Where(p => p.OrgId == orgId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            q = q.Where(p => p.Status == status);
        }
        if (CursorPaginator.TryDecode(cursor, out var ticks, out var raw)
            && Guid.TryParse(raw, out _))
        {
            var id = raw.ToLowerInvariant();
            q = q.Where(p => p.CreatedAtTicks < ticks
                || (p.CreatedAtTicks == ticks
                    && string.Compare(
                        p.Id.ToString().ToLower(), id) < 0));
        }

        var pageSize = Math.Clamp(limit, 1, 100);
        var rows = await q
            .OrderByDescending(p => p.CreatedAtTicks)
            .ThenByDescending(p => p.Id)
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

        var counts = await StateCountsAsync(
            rows.Select(r => r.Id).ToList(), ct);
        return new RefactorPlanPage(
            rows.Select(r =>
            {
                var c = counts.GetValueOrDefault(r.Id);
                return ToSummary(r, c.Total, c.ByState);
            }).ToList(), next);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RefactorBatchInfo>?> ListBatchesAsync(
        Guid orgId, Guid planId, CancellationToken ct)
    {
        var exists = await db.RefactorPlans.IgnoreQueryFilters()
            .AnyAsync(p => p.Id == planId && p.OrgId == orgId, ct);
        if (!exists)
        {
            return null;
        }

        var batches = await db.RefactorBatches
            .Where(b => b.PlanId == planId)
            .OrderBy(b => b.N)
            .ToListAsync(ct);
        return batches.Select(ToInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<string?> StartAsync(
        Guid orgId, Guid planId, Guid actorUserId, bool canApprove,
        CancellationToken ct)
    {
        var plan = await db.RefactorPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                p => p.Id == planId && p.OrgId == orgId, ct);
        if (plan is null)
        {
            return "not_found";
        }
        if (plan.Status != RefactorPlanStatuses.AwaitingApproval)
        {
            return "invalid_state";
        }
        // §9 hard gate: a plan is untrusted until a human with
        // refactors.approve reviews it.
        if (options.Value.RequirePlanApproval && !canApprove)
        {
            return "approval_required";
        }
        if (plan.PlanJson is null)
        {
            return "no_plan";
        }

        // Coordinator shell — parents every batch child (Run:false;
        // batch children get the default toolset: they must edit,
        // commit, push and open PRs).
        var repos = DeserializeRepos(plan.RepositoriesJson);
        var coordinatorTask = await conversations.CreateForUserAsync(
            actorUserId, orgId,
            new CreateConversationRequest(
                InitialMessage: new InitialMessage(
                    "user",
                    [$"Refactor coordinator for plan {plan.Id}: {plan.Goal}"],
                    Run: false),
                SelectedRepository: repos.FirstOrDefault(),
                AgentType: RefactorAgentTypes.Coordinator,
                LlmModel: null,
                SandboxSpecId: null,
                SelectedRepositories: repos),
            ct);

        plan.CoordinatorConversationId = coordinatorTask.AppConversationId;
        plan.Status = RefactorPlanStatuses.Running;
        plan.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await AuditAsync("refactor.started", actorUserId, orgId, plan.Id,
            new
            {
                approved = canApprove,
                coordinator = coordinatorTask.AppConversationId,
            }, ct);
        await dispatcher.EnqueueAsync(
            plan.Id, RefactorWorkKind.Execute, ct);
        return null;
    }

    /// <inheritdoc />
    public async Task<string?> PauseAsync(
        Guid orgId, Guid planId, CancellationToken ct) =>
        await TransitionPlanAsync(orgId, planId,
            RefactorPlanStatuses.Running, RefactorPlanStatuses.Paused,
            "refactor.paused", enqueue: false, ct);

    /// <inheritdoc />
    public async Task<string?> ResumeAsync(
        Guid orgId, Guid planId, CancellationToken ct) =>
        await TransitionPlanAsync(orgId, planId,
            RefactorPlanStatuses.Paused, RefactorPlanStatuses.Running,
            "refactor.resumed", enqueue: true, ct);

    /// <inheritdoc />
    public async Task<string?> CancelAsync(
        Guid orgId, Guid planId, CancellationToken ct)
    {
        var plan = await db.RefactorPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                p => p.Id == planId && p.OrgId == orgId, ct);
        if (plan is null)
        {
            return "not_found";
        }
        if (RefactorPlanStatuses.Terminal.Contains(plan.Status))
        {
            return "invalid_state";
        }

        plan.Status = RefactorPlanStatuses.Cancelled;
        plan.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await AuditAsync("refactor.cancelled", null, orgId, plan.Id,
            null, ct);
        return null;
    }

    private async Task<string?> TransitionPlanAsync(
        Guid orgId, Guid planId, string from, string to,
        string auditAction, bool enqueue, CancellationToken ct)
    {
        var plan = await db.RefactorPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                p => p.Id == planId && p.OrgId == orgId, ct);
        if (plan is null)
        {
            return "not_found";
        }
        if (plan.Status != from)
        {
            return "invalid_state";
        }

        plan.Status = to;
        await db.SaveChangesAsync(ct);
        await AuditAsync(auditAction, null, orgId, plan.Id, null, ct);
        if (enqueue)
        {
            await dispatcher.EnqueueAsync(
                plan.Id, RefactorWorkKind.Execute, ct);
        }
        return null;
    }

    /// <inheritdoc />
    public async Task<string?> RetryBatchAsync(
        Guid orgId, Guid planId, int batchN, CancellationToken ct)
    {
        var plan = await db.RefactorPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                p => p.Id == planId && p.OrgId == orgId, ct);
        if (plan is null)
        {
            return "not_found";
        }

        var batch = await db.RefactorBatches
            .FirstOrDefaultAsync(
                b => b.PlanId == planId && b.N == batchN, ct);
        if (batch is null)
        {
            return "batch_not_found";
        }
        if (batch.State != RefactorBatchStates.Failed)
        {
            return "invalid_state";
        }

        // failed → ready; blocked dependents walk back to pending and
        // re-evaluate on the next executor pass.
        batch.State = RefactorBatchStates.Ready;
        batch.Error = null;
        batch.UpdatedAt = DateTimeOffset.UtcNow;

        var dependents = await db.RefactorBatches
            .Where(b => b.PlanId == planId
                && b.State == RefactorBatchStates.Blocked)
            .ToListAsync(ct);
        var depNs = dependents
            .Where(b => RefactorService.DeserializeInts(b.DependsOnJson)
                .Contains(batchN))
            .ToList();
        foreach (var d in depNs)
        {
            d.State = RefactorBatchStates.Pending;
            d.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // A cancelled/completed plan never restarts from a retry.
        if (plan.Status == RefactorPlanStatuses.Failed)
        {
            plan.Status = RefactorPlanStatuses.Running;
            plan.FinishedAt = null;
        }
        await db.SaveChangesAsync(ct);
        await AuditAsync("refactor.batch_retried", null, orgId, planId,
            new { batch = batchN }, ct);
        if (plan.Status == RefactorPlanStatuses.Running)
        {
            await dispatcher.EnqueueAsync(
                planId, RefactorWorkKind.Execute, ct);
        }
        return null;
    }

    /// <inheritdoc />
    public async Task<bool> OnPullRequestMergedAsync(
        Guid orgId, string repository, int prNumber, CancellationToken ct)
    {
        var batch = await FindBatchByPrAsync(
            orgId, repository, prNumber, ct);
        if (batch is null)
        {
            return false;
        }

        var changed = false;
        if (batch.State == RefactorBatchStates.PrOpen)
        {
            batch.State = RefactorBatchStates.Merged;
            batch.UpdatedAt = DateTimeOffset.UtcNow;
            changed = true;
        }

        var plan = await db.RefactorPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == batch.PlanId, ct);
        if (plan is not null
            && !RefactorPlanStatuses.Terminal.Contains(plan.Status))
        {
            await db.SaveChangesAsync(ct);
            await AuditAsync("refactor.batch_merged", null, orgId,
                plan.Id, new
                {
                    batch = batch.N,
                    pr = batch.PrUrl,
                    repository,
                }, ct);
            // Dependents may unblock — re-enqueue execution.
            await dispatcher.EnqueueAsync(
                plan.Id, RefactorWorkKind.Execute, ct);
            return true;
        }
        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> OnPullRequestFeedbackAsync(
        Guid orgId, string repository, int prNumber, string feedback,
        CancellationToken ct)
    {
        var batch = await FindBatchByPrAsync(
            orgId, repository, prNumber, ct);
        if (batch is null || batch.State != RefactorBatchStates.PrOpen)
        {
            return batch is not null;
        }

        var redacted = SecretRedactor.Redact(feedback);
        if (redacted.Length > 3000)
        {
            redacted = redacted[..3000];
        }

        // delegate_message — só se a filha ainda estiver viva; caso
        // contrário registra deferral (SPEC-144 RF-002).
        var childAlive = batch.ConversationId is { } cid
            && await db.Conversations.IgnoreQueryFilters()
                .Where(c => c.Id == cid)
                .Select(c => c.ExecutionStatus)
                .FirstOrDefaultAsync(ct) is { } st
            && !BatchExecutor.TerminalConversationStatuses.Contains(st);

        var delivered = false;
        if (childAlive && events is not null)
        {
            try
            {
                await events.AppendAsync(batch.ConversationId!.Value,
                    new MessageEvent
                    {
                        Source = EventSource.User,
                        LlmMessage = JsonSerializer.SerializeToElement(
                            new
                            {
                                role = "user",
                                content = new object[]
                                {
                                    $"PR review feedback on batch "
                                    + $"{batch.N} ({batch.PrUrl}):\n\n"
                                    + redacted + "\n\nRebase the batch "
                                    + "branch, address the feedback and "
                                    + "re-run the verify command, then "
                                    + "report a new refactor_batch_v1 "
                                    + "block.",
                                },
                            }, EventJson.Options),
                    }, ct);
                if (runnerHost is not null)
                {
                    await runnerHost.NotifyMessageAsync(
                        batch.ConversationId.Value, ct);
                }
                delivered = true;
            }
            catch (Exception e)
            {
                logger.LogDebug(e,
                    "refactor feedback delivery failed (batch {Id})",
                    batch.Id);
            }
        }

        var plan = await db.RefactorPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == batch.PlanId, ct);
        if (plan is not null)
        {
            await AuditAsync(
                delivered
                    ? "refactor.feedback_forwarded"
                    : "refactor.feedback_deferred",
                null, orgId, plan.Id,
                new
                {
                    batch = batch.N,
                    pr = batch.PrUrl,
                    repository,
                    delivered,
                }, ct);
        }
        return true;
    }

    /// <inheritdoc />
    public async Task ReconcileAsync(CancellationToken ct)
    {
        // pr_open → merged|failed via provider poll — cobre webhooks
        // perdidos (mesmo padrão do ScanService.ReconcileRemediations).
        var open = await db.RefactorBatches
            .Where(b => b.State == RefactorBatchStates.PrOpen
                && b.PrNumber != null)
            .Take(200).ToListAsync(ct);
        if (open.Count == 0)
        {
            return;
        }

        var plans = await db.RefactorPlans.IgnoreQueryFilters()
            .Where(p => open.Select(b => b.PlanId).Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        var toEnqueue = new HashSet<Guid>();

        foreach (var b in open)
        {
            if (!plans.TryGetValue(b.PlanId, out var plan)
                || plan.OrgId is null)
            {
                continue;
            }

            var (merged, closed) = await PollPrAsync(
                plan.OrgId.Value, b.Repository, b.PrNumber!.Value, ct);
            if (merged)
            {
                b.State = RefactorBatchStates.Merged;
                b.UpdatedAt = DateTimeOffset.UtcNow;
                toEnqueue.Add(b.PlanId);
                await AuditAsync("refactor.batch_merged", null,
                    plan.OrgId, b.PlanId,
                    new { batch = b.N, pr = b.PrUrl, poll = true }, ct);
            }
            else if (closed)
            {
                b.State = RefactorBatchStates.Failed;
                b.Error = "pull request closed without merge";
                b.UpdatedAt = DateTimeOffset.UtcNow;
                await AuditAsync("refactor.batch_failed", null,
                    plan.OrgId, b.PlanId,
                    new
                    {
                        batch = b.N,
                        reason = "pr_closed_unmerged",
                    }, ct);
            }
        }

        await db.SaveChangesAsync(ct);
        foreach (var planId in toEnqueue)
        {
            await dispatcher.EnqueueAsync(
                planId, RefactorWorkKind.Execute, ct);
        }
    }

    /// <summary>(merged, closed) poll of a batch PR via the provider.</summary>
    private async Task<(bool Merged, bool Closed)> PollPrAsync(
        Guid orgId, string repository, int prNumber,
        CancellationToken ct)
    {
        if (connections is null || providers is null)
        {
            return (false, false);
        }

        try
        {
            var connection = await connections.FindForRepositoryAsync(
                orgId, ScmKind.GitHub, repository, ct)
                ?? await connections.FindForRepositoryAsync(
                    orgId, ScmKind.GitLab, repository, ct);
            if (connection is null)
            {
                return (false, false);
            }
            var provider = providers.Get(connection.Kind);
            var pr = provider is null
                ? null
                : await provider.GetPullRequestAsync(
                    connection, repository, prNumber, ct);
            if (pr is null)
            {
                return (false, false);
            }
            var closed = string.Equals(pr.State, "closed",
                    StringComparison.OrdinalIgnoreCase)
                || pr.Merged == true;
            return (pr.Merged == true, closed);
        }
        catch (Exception e)
        {
            logger.LogDebug(e,
                "refactor reconcile: pr poll failed {Repo}#{Pr}",
                repository, prNumber);
            return (false, false);
        }
    }

    private async Task<RefactorBatchRecord?> FindBatchByPrAsync(
        Guid orgId, string repository, int prNumber,
        CancellationToken ct)
    {
        var plans = await db.RefactorPlans.IgnoreQueryFilters()
            .Where(p => p.OrgId == orgId)
            .Select(p => p.Id)
            .ToListAsync(ct);
        return await db.RefactorBatches
            .Where(b => plans.Contains(b.PlanId)
                && b.PrNumber == prNumber
                && b.Repository == repository)
            .FirstOrDefaultAsync(ct);
    }

    private async Task AuditAsync(string action, Guid? actor,
        Guid? orgId, Guid targetId, object? metadata,
        CancellationToken ct)
    {
        if (audit is null)
        {
            return;
        }

        try
        {
            await audit.RecordAsync(action,
                actorUserId: actor?.ToString(), orgId: orgId,
                targetType: "refactor_plan",
                targetId: targetId.ToString(),
                metadata: metadata, cancellationToken: ct);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "refactor audit {Action} skipped", action);
        }
    }

    private async Task<Dictionary<Guid, (int Total,
        Dictionary<string, int> ByState)>> StateCountsAsync(
        List<Guid> planIds, CancellationToken ct)
    {
        if (planIds.Count == 0)
        {
            return [];
        }

        var rows = await db.RefactorBatches
            .Where(b => planIds.Contains(b.PlanId))
            .GroupBy(b => new { b.PlanId, b.State })
            .Select(g => new
            { g.Key.PlanId, g.Key.State, N = g.Count() })
            .ToListAsync(ct);

        var result = new Dictionary<Guid,
            (int, Dictionary<string, int>)>();
        foreach (var r in rows)
        {
            if (!result.TryGetValue(r.PlanId, out var t))
            {
                t = (0, new Dictionary<string, int>(
                    StringComparer.Ordinal));
            }
            t.Item2[r.State] = r.N;
            result[r.PlanId] = (t.Item1 + r.N, t.Item2);
        }
        return result;
    }

    internal static RefactorPlanSummary ToSummary(
        RefactorPlanRecord p, int total,
        IReadOnlyDictionary<string, int>? byState)
    {
        // Plans still in planning have no batch rows — null counts map
        // to an empty state dictionary (DTO contract: never null).
        var states = byState ?? new Dictionary<string, int>();
        var done = states.GetValueOrDefault(RefactorBatchStates.Merged);
        var pct = total == 0 ? 0
            : (int)Math.Round(done * 100.0 / total);
        return new RefactorPlanSummary(
            p.Id, p.OrgId, DeserializeRepos(p.RepositoriesJson), p.Goal,
            p.Strategy, p.Mode, p.RequestedMode, p.MaxConcurrency,
            p.BatchSizeHint, p.Status, total, states, pct,
            p.PlannerConversationId, p.CoordinatorConversationId,
            PlanWarnings(p.PlanJson), p.Error, p.CreatedAt, p.FinishedAt);
    }

    internal static RefactorBatchInfo ToInfo(
        RefactorBatchRecord b) => new(
        b.Id, b.PlanId, b.N, b.Repository,
        DeserializeStrings(b.FilesJson),
        DeserializeInts(b.DependsOnJson),
        b.Description, b.Risk, b.VerifyCommand, b.State, b.PrUrl,
        b.PrNumber, b.ConversationId, b.Attempts, b.VerifyLogDigest,
        b.Error, b.CreatedAt, b.UpdatedAt);

    internal static IReadOnlyList<string> DeserializeRepos(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<string> DeserializeStrings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<int> DeserializeInts(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<int>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Warnings embedded in <c>plan_json</c> (e.g. forced mode).</summary>
    internal static IReadOnlyList<string> PlanWarnings(string? planJson)
    {
        if (planJson is null)
        {
            return [];
        }
        try
        {
            using var doc = JsonDocument.Parse(planJson);
            if (!doc.RootElement.TryGetProperty(
                    "warnings", out var arr))
            {
                return [];
            }
            return arr.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}