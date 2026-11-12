using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Refactors;
using Sandcastle.Infrastructure.Refactors;
using Sandcastle.UnitTests.Scans;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Refactors;

/// <summary>
/// SPEC-144 RF-002: batch executor — dependency evaluation
/// (pending→ready on merged deps, →blocked on failed deps), bounded
/// fan-out (max_concurrency), the verify gate (refactor_batch_v1 →
/// pr_open|failed) and plan completion.
/// </summary>
public sealed class Spec144BatchExecutorTests
{
    private const string OkPayload = """
        ```refactor_batch
        {"schema":"refactor_batch_v1","verify_passed":true,
         "verify_attempts":1,"log_tail":"build ok",
         "summary":"done"}
        ```
        """;

    private const string FailPayload = """
        ```refactor_batch
        {"schema":"refactor_batch_v1","verify_passed":false,
         "verify_attempts":5,"log_tail":"CS0246: type not found",
         "summary":"verify failed"}
        ```
        """;

    private static RefactorPlanRecord SeedPlan(
        string status = RefactorPlanStatuses.Running,
        int maxConcurrency = 5) => new()
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            RepositoriesJson = "[\"org/repo\"]",
            Goal = "migrate",
            Strategy = RefactorStrategies.ImportGraph,
            Mode = RefactorModes.Independent,
            RequestedMode = RefactorModes.Independent,
            Status = status,
            PlanJson = "{}",
            MaxConcurrency = maxConcurrency,
            CoordinatorConversationId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static RefactorBatchRecord SeedBatch(
        Guid planId, int n, string state,
        string dependsOn = "[]", string? verify = "dotnet test") => new()
        {
            Id = Guid.NewGuid(),
            PlanId = planId,
            N = n,
            Repository = "org/repo",
            FilesJson = $"[\"src/F{n}.cs\"]",
            DependsOnJson = dependsOn,
            Risk = RefactorRisks.Low,
            VerifyCommand = verify,
            State = state,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static BatchExecutor NewExecutor(
        Sandcastle.Infrastructure.Persistence.AppDbContext db,
        Sandcastle.Application.Delegation.IDelegationService delegation,
        RecordingScanAudit? audit = null,
        RefactorOptions? options = null) =>
        new(db, delegation,
            Options.Create(options ?? new RefactorOptions
            {
                PollIntervalSeconds = 2,
                ChildTimeoutMinutes = 30,
            }),
            NullLogger<BatchExecutor>.Instance, audit);

    // ---------- EvaluateDependencies (pure state machine) ----------

    [Fact]
    public void Pending_Becomes_Ready_When_All_Deps_Merged()
    {
        var dep = SeedBatch(Guid.NewGuid(), 1, RefactorBatchStates.Merged);
        var child = SeedBatch(dep.PlanId, 2, RefactorBatchStates.Pending,
            dependsOn: "[1]");

        BatchExecutor.EvaluateDependencies([dep, child]);
        child.State.ShouldBe(RefactorBatchStates.Ready);
    }

    [Fact]
    public void Pending_Stays_When_Dep_Not_Merged()
    {
        var planId = Guid.NewGuid();
        var dep = SeedBatch(planId, 1, RefactorBatchStates.PrOpen);
        var child = SeedBatch(planId, 2, RefactorBatchStates.Pending,
            dependsOn: "[1]");

        BatchExecutor.EvaluateDependencies([dep, child]);
        child.State.ShouldBe(RefactorBatchStates.Pending);
    }

    [Fact]
    public void Pending_Or_Ready_Blocks_When_Dep_Failed()
    {
        var planId = Guid.NewGuid();
        var dep = SeedBatch(planId, 1, RefactorBatchStates.Failed);
        var pending = SeedBatch(planId, 2, RefactorBatchStates.Pending,
            dependsOn: "[1]");
        var ready = SeedBatch(planId, 3, RefactorBatchStates.Ready,
            dependsOn: "[1]");

        BatchExecutor.EvaluateDependencies([dep, pending, ready]);
        pending.State.ShouldBe(RefactorBatchStates.Blocked);
        ready.State.ShouldBe(RefactorBatchStates.Blocked);
    }

    [Fact]
    public void Dependency_Chain_Unblocks_Transitively()
    {
        var planId = Guid.NewGuid();
        var b1 = SeedBatch(planId, 1, RefactorBatchStates.Merged);
        var b2 = SeedBatch(planId, 2, RefactorBatchStates.Pending,
            dependsOn: "[1]");
        var b3 = SeedBatch(planId, 3, RefactorBatchStates.Pending,
            dependsOn: "[2]");

        // b2 readies in the same pass; b3 needs b2 *merged* — stays.
        BatchExecutor.EvaluateDependencies([b1, b2, b3]);
        b2.State.ShouldBe(RefactorBatchStates.Ready);
        b3.State.ShouldBe(RefactorBatchStates.Pending);
    }

    // ---------- Executor loop ----------

    [Fact]
    public async Task Dispatch_Then_Collect_Opens_Pr()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;

        var plan = SeedPlan();
        db.RefactorPlans.Add(plan);
        var b1 = SeedBatch(plan.Id, 1, RefactorBatchStates.Ready);
        db.RefactorBatches.Add(b1);
        await db.SaveChangesAsync();

        var delegation = new FakeRefactorDelegation(db, OkPayload);
        var audit = new RecordingScanAudit();
        await NewExecutor(db, delegation, audit)
            .RunAsync(plan.Id, CancellationToken.None);

        delegation.Calls.Count.ShouldBe(1);
        delegation.Calls[0].Req.AgentType.ShouldBe(
            RefactorAgentTypes.Worker);
        delegation.Calls[0].Req.SelectedRepository.ShouldBe("org/repo");

        var batch = await db.RefactorBatches.SingleAsync(
            b => b.Id == b1.Id);
        batch.State.ShouldBe(RefactorBatchStates.PrOpen);
        batch.PrUrl.ShouldBe("https://github.com/org/repo/pull/42");
        batch.PrNumber.ShouldBe(42);
        batch.ConversationId.ShouldNotBeNull();
        batch.Attempts.ShouldBe(1);
        batch.VerifyLogDigest.ShouldNotBeNull();
        audit.Actions.ShouldContain("refactor.batch_dispatched");
    }

    [Fact]
    public async Task Verify_Failure_Fails_Batch_With_Digest()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;

        var plan = SeedPlan();
        db.RefactorPlans.Add(plan);
        var b1 = SeedBatch(plan.Id, 1, RefactorBatchStates.Ready);
        db.RefactorBatches.Add(b1);
        await db.SaveChangesAsync();

        var delegation = new FakeRefactorDelegation(db, FailPayload);
        var audit = new RecordingScanAudit();
        await NewExecutor(db, delegation, audit)
            .RunAsync(plan.Id, CancellationToken.None);

        var batch = await db.RefactorBatches.SingleAsync(
            b => b.Id == b1.Id);
        batch.State.ShouldBe(RefactorBatchStates.Failed);
        batch.Error!.ShouldContain("verify failed");
        batch.VerifyLogDigest.ShouldNotBeNull();
        audit.Actions.ShouldContain("refactor.batch_failed");
    }

    [Fact]
    public async Task Missing_Output_Fails_Batch()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;

        var plan = SeedPlan();
        db.RefactorPlans.Add(plan);
        db.RefactorBatches.Add(
            SeedBatch(plan.Id, 1, RefactorBatchStates.Ready));
        await db.SaveChangesAsync();

        // Child finishes without the refactor_batch_v1 block.
        var delegation = new FakeRefactorDelegation(db, payload: null);
        await NewExecutor(db, delegation)
            .RunAsync(plan.Id, CancellationToken.None);

        (await db.RefactorBatches.SingleAsync()).State
            .ShouldBe(RefactorBatchStates.Failed);
    }

    [Fact]
    public async Task Concurrency_Cap_Limits_In_Flight_Children()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;

        var plan = SeedPlan(maxConcurrency: 2);
        db.RefactorPlans.Add(plan);
        for (var n = 1; n <= 4; n++)
        {
            db.RefactorBatches.Add(
                SeedBatch(plan.Id, n, RefactorBatchStates.Ready));
        }
        await db.SaveChangesAsync();

        // Children stay "Running" — slots never free; only the cap
        // number of dispatches may happen.
        var delegation = new FakeRefactorDelegation(
            db, materializedStatus: "Running");
        using var cts = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        try
        {
            await NewExecutor(db, delegation,
                options: new RefactorOptions
                {
                    PollIntervalSeconds = 2,
                    ChildTimeoutMinutes = 30,
                }).RunAsync(plan.Id, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        delegation.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Failed_Dep_Blocks_Dependent_Without_Dispatch()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;

        var plan = SeedPlan();
        db.RefactorPlans.Add(plan);
        db.RefactorBatches.Add(
            SeedBatch(plan.Id, 1, RefactorBatchStates.Failed));
        var dependent = SeedBatch(
            plan.Id, 2, RefactorBatchStates.Pending, dependsOn: "[1]");
        db.RefactorBatches.Add(dependent);
        await db.SaveChangesAsync();

        var delegation = new FakeRefactorDelegation(db, OkPayload);
        await NewExecutor(db, delegation)
            .RunAsync(plan.Id, CancellationToken.None);

        delegation.Calls.ShouldBeEmpty();
        (await db.RefactorBatches.SingleAsync(b => b.N == 2)).State
            .ShouldBe(RefactorBatchStates.Blocked);
    }

    [Fact]
    public async Task All_Merged_Completes_Plan()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;

        var plan = SeedPlan();
        db.RefactorPlans.Add(plan);
        db.RefactorBatches.Add(
            SeedBatch(plan.Id, 1, RefactorBatchStates.Merged));
        db.RefactorBatches.Add(
            SeedBatch(plan.Id, 2, RefactorBatchStates.Merged,
                dependsOn: "[1]"));
        await db.SaveChangesAsync();

        var audit = new RecordingScanAudit();
        await NewExecutor(db, new FakeRefactorDelegation(db), audit)
            .RunAsync(plan.Id, CancellationToken.None);

        var fresh = await db.RefactorPlans.SingleAsync();
        fresh.Status.ShouldBe(RefactorPlanStatuses.Completed);
        fresh.StatsJson.ShouldNotBeNull();
        audit.Actions.ShouldContain("refactor.completed");
    }

    [Fact]
    public async Task Paused_Plan_Stops_The_Pass()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;

        var plan = SeedPlan(status: RefactorPlanStatuses.Paused);
        db.RefactorPlans.Add(plan);
        db.RefactorBatches.Add(
            SeedBatch(plan.Id, 1, RefactorBatchStates.Ready));
        await db.SaveChangesAsync();

        var delegation = new FakeRefactorDelegation(db, OkPayload);
        await NewExecutor(db, delegation)
            .RunAsync(plan.Id, CancellationToken.None);

        delegation.Calls.ShouldBeEmpty();
        (await db.RefactorBatches.SingleAsync()).State
            .ShouldBe(RefactorBatchStates.Ready);
    }
}