using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Eval;
using Sandcastle.Infrastructure.Eval;
using Sandcastle.Infrastructure.Persistence;

using Shouldly;

namespace Sandcastle.UnitTests.Eval;

/// <summary>
/// SPEC-152 RF-001/RF-004 — EvalService sobre Sqlite :memory::
/// create (n clamp + tasks_path + suite inválida), dedup por chave,
/// cancel (queued→cancelled, terminal→409), estimate (pricing do
/// catálogo + gate &gt;25 tasks), cap diário e scorecard por
/// model/route.
/// </summary>
public sealed class Spec152EvalServiceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly AppDbContext _db;
    private readonly FakeDispatcher _dispatcher = new();
    private readonly EvalOptions _opt = new()
    {
        MaxTasks = 200,
        EstimateGateTasks = 25,
        EstimatedPromptTokensPerTask = 1000,
        EstimatedCompletionTokensPerTask = 500,
        MaxDailyCostUsd = null,
    };

    public Spec152EvalServiceTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private sealed class FakeDispatcher : IEvalDispatcher
    {
        public List<Guid> Enqueued { get; } = [];
        public ValueTask EnqueueAsync(Guid runId, CancellationToken ct)
        {
            Enqueued.Add(runId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAdapter(int count) : IEvalSuiteAdapter
    {
        // Suite "custom" — nome válido do registry; TasksPath no
        // request é ignorado pelo fake (retorna N sintéticas).
        public string Suite => EvalSuites.Custom;
        public Task<IReadOnlyList<EvalTaskV1>> LoadAsync(
            EvalSuiteLoadContext ctx, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EvalTaskV1>>(
                Enumerable.Range(1, count).Select(i => new EvalTaskV1
                {
                    TaskId = $"t{i}",
                    Image = "img",
                    Prompt = "p",
                    Checker = new EvalTaskChecker { Cmd = "c" },
                }).ToList());
    }

    private sealed class FakeResolver(IEvalSuiteAdapter adapter)
        : IEvalSuiteResolver
    {
        public IEvalSuiteAdapter? Get(string suite) =>
            suite == adapter.Suite ? adapter : null;
    }

    private sealed class FakeCatalog(ModelInfo? info) : IModelCatalog
    {
        public Task<IReadOnlyList<ModelInfo>> ListAsync(
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>(
                info is null ? [] : [info]);
        public Task<ModelInfo?> GetAsync(
            string modelId, CancellationToken ct) =>
            Task.FromResult(info);
        public ModelInfo? TryGetCached(string modelId) => info;
    }

    private EvalService Svc(int tasks = 5, IModelCatalog? catalog = null,
        EvalOptions? opt = null) =>
        new(_db, _dispatcher,
            new FakeResolver(new FakeAdapter(tasks)),
            Options.Create(opt ?? _opt), catalog);

    private static CreateEvalRunRequest Req(
        string suite = EvalSuites.Custom,
        int? n = 3, string? model = "m1", bool ack = true) =>
        new(suite, model, null, n, "tasks.jsonl", ack);

    [Fact]
    public async Task Create_Queues_Run_And_Enqueues()
    {
        var (created, error, gate) = await Svc(tasks: 5)
            .CreateAsync(Req(n: 3), Guid.NewGuid(), Guid.NewGuid(),
                CancellationToken.None);

        error.ShouldBeNull();
        gate.ShouldBeNull();
        created.ShouldNotBeNull();
        created.Status.ShouldBe("queued");
        created.NTasks.ShouldBe(3);
        _dispatcher.Enqueued.ShouldBe([created.RunId]);
        var run = await _db.EvalRuns.FindAsync(created.RunId);
        run!.Suite.ShouldBe(EvalSuites.Custom);
        run.Model.ShouldBe("m1");
    }

    [Fact]
    public async Task Create_Clamps_N_To_Suite_Size_And_MaxTasks()
    {
        var (created, _, _) = await Svc(tasks: 2).CreateAsync(
            Req(n: 50), Guid.NewGuid(), Guid.NewGuid(),
            CancellationToken.None);
        created!.NTasks.ShouldBe(2);

        var (c2, error, _) = await Svc(tasks: 500).CreateAsync(
            Req(n: 300), Guid.NewGuid(), Guid.NewGuid(),
            CancellationToken.None);
        c2.ShouldBeNull();
        error.ShouldStartWith("max_tasks_exceeded");
    }

    [Fact]
    public async Task Create_Dedup_Returns_Same_Run_While_Active()
    {
        var org = Guid.NewGuid();
        var user = Guid.NewGuid();
        var (first, _, _) = await Svc().CreateAsync(
            Req(), user, org, CancellationToken.None);
        var (second, error, _) = await Svc().CreateAsync(
            Req(), user, org, CancellationToken.None);

        error.ShouldBeNull();
        second!.RunId.ShouldBe(first!.RunId);
        _dispatcher.Enqueued.ShouldHaveSingleItem();
        (await _db.EvalRuns.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Create_Validation_Errors()
    {
        var user = Guid.NewGuid();
        (await Svc().CreateAsync(Req(suite: "bogus"), user, null,
            CancellationToken.None)).Error.ShouldBe("invalid_suite");
        (await Svc().CreateAsync(Req(n: 0), user, null,
            CancellationToken.None)).Error.ShouldBe("invalid_n");
        (await Svc().CreateAsync(
            new CreateEvalRunRequest("custom", N: 1),
            user, null, CancellationToken.None)).Error
            .ShouldBe("tasks_path_required");
    }

    [Fact]
    public async Task Create_EstimateGate_Requires_Acknowledgement()
    {
        var (created, error, gate) = await Svc(tasks: 30)
            .CreateAsync(Req(n: 30, ack: false), Guid.NewGuid(),
                Guid.NewGuid(), CancellationToken.None);

        created.ShouldBeNull();
        error.ShouldBe("estimate_required");
        gate.ShouldNotBeNull();
        gate.RequiresAcknowledgement.ShouldBeTrue();
        gate.N.ShouldBe(30);

        // Com ack a run é criada.
        var (ok, _, _) = await Svc(tasks: 30).CreateAsync(
            Req(n: 30, ack: true), Guid.NewGuid(), Guid.NewGuid(),
            CancellationToken.None);
        ok.ShouldNotBeNull();
    }

    [Fact]
    public async Task Estimate_Uses_Model_Pricing_When_Known()
    {
        var catalog = new FakeCatalog(new ModelInfo("m1", "p",
            Pricing: new ModelPricing(2m, 10m))); // $/1M tokens
        var est = await Svc(tasks: 5, catalog: catalog)
            .EstimateAsync(new EvalEstimateRequest(
                EvalSuites.Custom, 100, "m1"), Guid.NewGuid(),
                CancellationToken.None);

        // 100 × (1000×2 + 500×10) / 1M = 100 × 0.007 = 0.7
        est!.EstimatedCostUsd.ShouldBe(0.7m);
        est.PricingKnown.ShouldBeTrue();
        est.RequiresAcknowledgement.ShouldBeTrue(); // 100 > 25
    }

    [Fact]
    public async Task Estimate_Unknown_Pricing_Returns_Null_Cost()
    {
        var est = await Svc(tasks: 5).EstimateAsync(
            new EvalEstimateRequest(EvalSuites.Custom, 10, "m9"),
            null, CancellationToken.None);
        est!.EstimatedCostUsd.ShouldBeNull();
        est.PricingKnown.ShouldBeFalse();
        est.Warning.ShouldNotBeNull();
    }

    [Fact]
    public async Task Create_DailyCostCap_Rejects_When_Exceeded()
    {
        var org = Guid.NewGuid();
        var opt = new EvalOptions
        {
            MaxTasks = 200,
            EstimateGateTasks = 25,
            EstimatedPromptTokensPerTask = 1000,
            EstimatedCompletionTokensPerTask = 500,
            MaxDailyCostUsd = 0.0001m,
        };
        var catalog = new FakeCatalog(new ModelInfo("m1", "p",
            Pricing: new ModelPricing(2m, 10m)));
        var (_, error, _) = await Svc(tasks: 5,
            catalog: catalog, opt: opt).CreateAsync(
                Req(n: 3), Guid.NewGuid(), org,
                CancellationToken.None);
        error.ShouldStartWith("daily_cost_exceeded");
    }

    [Fact]
    public async Task Cancel_Queued_Becomes_Cancelled_Terminal_409()
    {
        var org = Guid.NewGuid();
        var (created, _, _) = await Svc().CreateAsync(
            Req(), Guid.NewGuid(), org, CancellationToken.None);

        (await Svc().CancelAsync(org, created!.RunId,
            CancellationToken.None)).ShouldBeNull();
        (await _db.EvalRuns.FindAsync(created.RunId))!.Status
            .ShouldBe("cancelled");
        // Segunda chamada — terminal → conflito.
        (await Svc().CancelAsync(org, created.RunId,
            CancellationToken.None)).ShouldBe("terminal");
        (await Svc().CancelAsync(org, Guid.NewGuid(),
            CancellationToken.None)).ShouldBe("not_found");
    }

    [Fact]
    public async Task Scorecard_Groups_By_Model_Route()
    {
        var org = Guid.NewGuid();
        foreach (var (model, route, resolved, n) in new[]
        {
            ("m1", (string?)"rA", 8, 10),
            ("m1", (string?)"rA", 9, 10),
            ("m2", (string?)null, 4, 10),
        })
        {
            _db.EvalRuns.Add(new EvalRunRecord
            {
                Id = Guid.NewGuid(),
                OrgId = org,
                Suite = "swe-lite",
                NTasks = n,
                Model = model,
                RouteOverride = route,
                Status = "completed",
                ResolvedN = resolved,
                ScorePct = 100.0 * resolved / n,
                CreatedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow,
            });
        }

        await _db.SaveChangesAsync();
        var rows = await Svc().ScorecardAsync(
            org, "swe-lite", 10, CancellationToken.None);

        rows.Count.ShouldBe(2);
        var m1 = rows.First(r => r.Model == "m1");
        m1.Runs.ShouldBe(2);
        m1.Tasks.ShouldBe(20);
        m1.Resolved.ShouldBe(17);
        m1.ResolvedPct.ShouldBe(85.0);
        rows.First(r => r.Model == "m2").ResolvedPct.ShouldBe(40.0);
    }
}