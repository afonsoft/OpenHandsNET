using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Refactors;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Refactors;
using Sandcastle.UnitTests.Agent;
using Sandcastle.UnitTests.Scans;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Refactors;

/// <summary>
/// SPEC-144 RF-001/RF-003/RF-004: refactor service — request validation,
/// planner/coordinator conversation shells, the human approval gate,
/// merge-webhook unblocking, batch retry and PR-feedback forwarding.
/// </summary>
public sealed class Spec144RefactorServiceTests
{
    private static CreateRefactorRequest Req(
        IReadOnlyList<string>? repos = null,
        string goal = "migrate controllers to minimal APIs",
        string? strategy = null, string? mode = null,
        int? concurrency = null, int? sizeHint = null) => new(
        repos ?? ["org/repo"], goal, strategy, concurrency, mode,
        sizeHint);

    private static RefactorService NewService(
        AppDbContext db,
        FakeConversationService convs,
        FakeRefactorDispatcher dispatcher,
        FakeEventService? events = null,
        FakeRunnerHost? host = null,
        RecordingScanAudit? audit = null,
        RefactorOptions? options = null) =>
        new(db, convs, dispatcher,
            Options.Create(options ?? new RefactorOptions()),
            NullLogger<RefactorService>.Instance,
            events, host, audit: audit);

    private static async Task<RefactorPlanRecord> SeedPlanAsync(
        AppDbContext db, string status, string? planJson = null,
        Guid? orgId = null, string mode = RefactorModes.Independent)
    {
        var plan = new RefactorPlanRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId ?? Guid.NewGuid(),
            CreatedByUserId = Guid.NewGuid().ToString(),
            RepositoriesJson = "[\"org/repo\"]",
            Goal = "goal",
            Strategy = RefactorStrategies.ImportGraph,
            Mode = mode,
            RequestedMode = mode,
            MaxConcurrency = 5,
            Status = status,
            PlanJson = planJson,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.RefactorPlans.Add(plan);
        await db.SaveChangesAsync();
        return plan;
    }

    private static RefactorBatchRecord SeedBatch(
        Guid planId, int n, string state,
        string dependsOn = "[]", string repo = "org/repo",
        int? prNumber = null, Guid? convId = null) => new()
        {
            Id = Guid.NewGuid(),
            PlanId = planId,
            N = n,
            Repository = repo,
            FilesJson = $"[\"src/F{n}.cs\"]",
            DependsOnJson = dependsOn,
            Risk = RefactorRisks.Low,
            VerifyCommand = "dotnet test",
            State = state,
            PrUrl = prNumber is null ? null
                : $"https://github.com/{repo}/pull/{prNumber}",
            PrNumber = prNumber,
            ConversationId = convId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    // ---------- create ----------

    [Fact]
    public async Task Create_Spawns_Planner_Shell_And_Enqueues()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;
        var convs = new FakeConversationService(db);
        var dispatcher = new FakeRefactorDispatcher();
        var orgId = Guid.NewGuid();

        var (created, error) = await NewService(db, convs, dispatcher)
            .CreateAsync(Req(), Guid.NewGuid(), orgId,
                CancellationToken.None);

        error.ShouldBeNull();
        created.ShouldNotBeNull();
        convs.LastRequest!.AgentType.ShouldBe(RefactorAgentTypes.Planner);
        convs.LastRequest.InitialMessage!.Run.ShouldBe(false);
        convs.LastRequest.SelectedRepositories.ShouldBe(["org/repo"]);

        var plan = await db.RefactorPlans.SingleAsync(
            p => p.Id == created.PlanId);
        plan.Status.ShouldBe(RefactorPlanStatuses.Planning);
        plan.OrgId.ShouldBe(orgId);
        plan.Mode.ShouldBe(RefactorModes.Independent);
        plan.MaxConcurrency.ShouldBe(5);
        plan.PlannerConversationId.ShouldBe(created.PlannerConversationId);
        dispatcher.Enqueued.ShouldBe(
            [(created.PlanId, RefactorWorkKind.Plan)]);
    }

    [Fact]
    public async Task Create_Validates_Required_Fields()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;
        var svc = NewService(db, new FakeConversationService(db),
            new FakeRefactorDispatcher());
        var org = Guid.NewGuid();
        var uid = Guid.NewGuid();

        (await svc.CreateAsync(Req(goal: "  "), uid, org,
            CancellationToken.None)).Error!.ShouldContain("goal");
        (await svc.CreateAsync(Req(strategy: "bogus"), uid, org,
            CancellationToken.None)).Error!.ShouldContain("strategy");
        (await svc.CreateAsync(Req(mode: "bogus"), uid, org,
            CancellationToken.None)).Error!.ShouldContain("mode");
        (await svc.CreateAsync(Req(repos: []), uid, org,
            CancellationToken.None)).Error!.ShouldContain("repositor");
        (await svc.CreateAsync(Req(sizeHint: -2), uid, org,
            CancellationToken.None)).Error!.ShouldContain("batch_size");
    }

    [Fact]
    public async Task Create_Stacked_Rejects_Multi_Repo()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;
        var svc = NewService(db, new FakeConversationService(db),
            new FakeRefactorDispatcher());

        var (created, error) = await svc.CreateAsync(
            Req(repos: ["org/a", "org/b"], mode: "stacked"),
            Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        created.ShouldBeNull();
        error!.ShouldContain("single repository");
    }

    [Fact]
    public async Task Create_Clamps_Concurrency_To_Cap()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;
        var svc = NewService(db, new FakeConversationService(db),
            new FakeRefactorDispatcher());

        var (created, _) = await svc.CreateAsync(
            Req(concurrency: 99), Guid.NewGuid(), Guid.NewGuid(),
            CancellationToken.None);

        (await db.RefactorPlans.SingleAsync(p => p.Id == created!.PlanId))
            .MaxConcurrency.ShouldBe(8);
    }

    // ---------- start (approval gate) ----------

    [Fact]
    public async Task Start_Requires_Approve_Permission()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var plan = await SeedPlanAsync(db,
            RefactorPlanStatuses.AwaitingApproval,
            planJson: "{\"schema\":\"refactor_plan_v1\"}", orgId: org);
        var svc = NewService(db, new FakeConversationService(db),
            new FakeRefactorDispatcher());

        (await svc.StartAsync(org, plan.Id, Guid.NewGuid(),
            canApprove: false, CancellationToken.None))
            .ShouldBe("approval_required");

        // Gate disabled → member run permission suffices.
        var svcNoGate = NewService(db, new FakeConversationService(db),
            new FakeRefactorDispatcher(),
            options: new RefactorOptions { RequirePlanApproval = false });
        (await svcNoGate.StartAsync(org, plan.Id, Guid.NewGuid(),
            canApprove: false, CancellationToken.None))
            .ShouldBeNull();
        (await db.RefactorPlans.SingleAsync(p => p.Id == plan.Id))
            .Status.ShouldBe(RefactorPlanStatuses.Running);
    }

    [Fact]
    public async Task Start_Spawns_Coordinator_And_Enqueues()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var plan = await SeedPlanAsync(db,
            RefactorPlanStatuses.AwaitingApproval,
            planJson: "{}", orgId: org);
        var convs = new FakeConversationService(db);
        var dispatcher = new FakeRefactorDispatcher();

        var error = await NewService(db, convs, dispatcher)
            .StartAsync(org, plan.Id, Guid.NewGuid(),
                canApprove: true, CancellationToken.None);

        error.ShouldBeNull();
        convs.LastRequest!.AgentType
            .ShouldBe(RefactorAgentTypes.Coordinator);
        var fresh = await db.RefactorPlans.SingleAsync(
            p => p.Id == plan.Id);
        fresh.Status.ShouldBe(RefactorPlanStatuses.Running);
        fresh.CoordinatorConversationId.ShouldNotBeNull();
        dispatcher.Enqueued.ShouldBe(
            [(plan.Id, RefactorWorkKind.Execute)]);
    }

    [Fact]
    public async Task Start_Rejects_Non_Awaiting_States()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var plan = await SeedPlanAsync(
            db, RefactorPlanStatuses.Planning, orgId: org);
        var svc = NewService(db, new FakeConversationService(db),
            new FakeRefactorDispatcher());

        (await svc.StartAsync(org, plan.Id, Guid.NewGuid(),
            canApprove: true, CancellationToken.None))
            .ShouldBe("invalid_state");
        (await svc.StartAsync(org, Guid.NewGuid(), Guid.NewGuid(),
            canApprove: true, CancellationToken.None))
            .ShouldBe("not_found");
    }

    // ---------- merge webhook ----------

    [Fact]
    public async Task Merge_Webhook_Marks_Batch_And_Reenqueues()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var plan = await SeedPlanAsync(
            db, RefactorPlanStatuses.Running, orgId: org);
        db.RefactorBatches.Add(SeedBatch(plan.Id, 1,
            RefactorBatchStates.PrOpen, prNumber: 42));
        db.RefactorBatches.Add(SeedBatch(plan.Id, 2,
            RefactorBatchStates.Pending, dependsOn: "[1]"));
        await db.SaveChangesAsync();

        var dispatcher = new FakeRefactorDispatcher();
        var audit = new RecordingScanAudit();
        var svc = NewService(db, new FakeConversationService(db),
            dispatcher, audit: audit);

        (await svc.OnPullRequestMergedAsync(
            org, "org/repo", 42, CancellationToken.None))
            .ShouldBeTrue();

        (await db.RefactorBatches.SingleAsync(b => b.N == 1)).State
            .ShouldBe(RefactorBatchStates.Merged);
        dispatcher.Enqueued.ShouldBe(
            [(plan.Id, RefactorWorkKind.Execute)]);
        audit.Actions.ShouldContain("refactor.batch_merged");

        // The next executor pass readies the dependent.
        var all = await db.RefactorBatches.ToListAsync();
        BatchExecutor.EvaluateDependencies(all);
        all.Single(b => b.N == 2).State
            .ShouldBe(RefactorBatchStates.Ready);
    }

    [Fact]
    public async Task Merge_Webhook_Ignores_Unknown_Pr()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;
        var svc = NewService(db, new FakeConversationService(db),
            new FakeRefactorDispatcher());

        (await svc.OnPullRequestMergedAsync(
            Guid.NewGuid(), "org/repo", 7, CancellationToken.None))
            .ShouldBeFalse();
    }

    // ---------- retry ----------

    [Fact]
    public async Task Retry_Resets_Failed_And_Unblocks_Dependents()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var plan = await SeedPlanAsync(
            db, RefactorPlanStatuses.Running, orgId: org);
        db.RefactorBatches.Add(SeedBatch(plan.Id, 1,
            RefactorBatchStates.Failed));
        db.RefactorBatches.Add(SeedBatch(plan.Id, 2,
            RefactorBatchStates.Blocked, dependsOn: "[1]"));
        await db.SaveChangesAsync();

        var dispatcher = new FakeRefactorDispatcher();
        var svc = NewService(db, new FakeConversationService(db),
            dispatcher);

        (await svc.RetryBatchAsync(org, plan.Id, 1,
            CancellationToken.None)).ShouldBeNull();

        var batches = await db.RefactorBatches
            .OrderBy(b => b.N).ToListAsync();
        batches[0].State.ShouldBe(RefactorBatchStates.Ready);
        batches[1].State.ShouldBe(RefactorBatchStates.Pending);
        dispatcher.Enqueued.ShouldBe(
            [(plan.Id, RefactorWorkKind.Execute)]);
    }

    [Fact]
    public async Task Retry_Rejects_Non_Failed_Batch()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var plan = await SeedPlanAsync(
            db, RefactorPlanStatuses.Running, orgId: org);
        db.RefactorBatches.Add(SeedBatch(plan.Id, 1,
            RefactorBatchStates.PrOpen));
        await db.SaveChangesAsync();
        var svc = NewService(db, new FakeConversationService(db),
            new FakeRefactorDispatcher());

        (await svc.RetryBatchAsync(org, plan.Id, 1,
            CancellationToken.None)).ShouldBe("invalid_state");
        (await svc.RetryBatchAsync(org, plan.Id, 99,
            CancellationToken.None)).ShouldBe("batch_not_found");
    }

    // ---------- PR feedback → delegate_message ----------

    [Fact]
    public async Task Feedback_Forwards_To_Live_Child()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var plan = await SeedPlanAsync(
            db, RefactorPlanStatuses.Running, orgId: org);
        var childId = Guid.NewGuid();
        db.Conversations.Add(new ConversationRecord
        {
            Id = childId,
            ExecutionStatus = "Running",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CreatedAtTicks = DateTimeOffset.UtcNow.UtcTicks,
            UpdatedAtTicks = DateTimeOffset.UtcNow.UtcTicks,
        });
        db.RefactorBatches.Add(SeedBatch(plan.Id, 1,
            RefactorBatchStates.PrOpen, prNumber: 42,
            convId: childId));
        await db.SaveChangesAsync();

        var events = new FakeEventService();
        var host = new FakeRunnerHost();
        var audit = new RecordingScanAudit();
        var svc = NewService(db, new FakeConversationService(db),
            new FakeRefactorDispatcher(), events, host, audit);

        (await svc.OnPullRequestFeedbackAsync(org, "org/repo", 42,
            "please rebase and fix the build", CancellationToken.None))
            .ShouldBeTrue();

        events.Events.Count.ShouldBe(1);
        host.Notified.ShouldBe([childId]);
        audit.Actions.ShouldContain("refactor.feedback_forwarded");
    }

    [Fact]
    public async Task Feedback_Defers_When_Child_Dead()
    {
        var (conn, db) = await RefactorTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var plan = await SeedPlanAsync(
            db, RefactorPlanStatuses.Running, orgId: org);
        var childId = Guid.NewGuid();
        db.Conversations.Add(new ConversationRecord
        {
            Id = childId,
            ExecutionStatus = "Finished",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CreatedAtTicks = DateTimeOffset.UtcNow.UtcTicks,
            UpdatedAtTicks = DateTimeOffset.UtcNow.UtcTicks,
        });
        db.RefactorBatches.Add(SeedBatch(plan.Id, 1,
            RefactorBatchStates.PrOpen, prNumber: 42,
            convId: childId));
        await db.SaveChangesAsync();

        var events = new FakeEventService();
        var audit = new RecordingScanAudit();
        var svc = NewService(db, new FakeConversationService(db),
            new FakeRefactorDispatcher(), events,
            new FakeRunnerHost(), audit);

        (await svc.OnPullRequestFeedbackAsync(org, "org/repo", 42,
            "looks good but please rebase", CancellationToken.None))
            .ShouldBeTrue();

        events.Events.ShouldBeEmpty();
        audit.Actions.ShouldContain("refactor.feedback_deferred");
    }
}