using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Identity;
using Sandcastle.Infrastructure.Agent.Routing;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Agent;

/// <summary>SPEC-149: schema <c>routing_rules_v1</c> — parse + validação.</summary>
public sealed class Spec149RoutingRulesTests
{
    [Fact]
    public void Parse_Null_Or_Empty_Returns_Null()
    {
        RoutingRulesDocument.Parse(null).ShouldBeNull();
        RoutingRulesDocument.Parse("  ").ShouldBeNull();
    }

    [Fact]
    public void Parse_Round_Trips_A_Document()
    {
        var doc = new RoutingRulesDocument
        {
            Rules = new Dictionary<string, List<RouteCandidate>>
            {
                ["code"] =
                [
                    new RouteCandidate("gpt-5", "openai",
                        MaxCostPer1M: 8m, BudgetTier: "cheap"),
                    new RouteCandidate("claude-opus", "anthropic",
                        Weight: 3),
                ],
            },
            AllowedModels = ["gpt-5", "claude-opus"],
            DenyModels = ["gpt-4o"],
            DataResidency = "internal-only",
            CheapWhenBelowPct = 15,
        };

        var parsed = RoutingRulesDocument.Parse(doc.ToJson());

        parsed.ShouldNotBeNull();
        parsed.Version.ShouldBe(RoutingRulesDocument.CurrentVersion);
        parsed.Rules["code"].Count.ShouldBe(2);
        parsed.Rules["code"][0].BudgetTier.ShouldBe("cheap");
        parsed.Rules["code"][1].Weight.ShouldBe(3);
        parsed.AllowedModels.ShouldBe(["gpt-5", "claude-opus"]);
        parsed.DenyModels.ShouldBe(["gpt-4o"]);
        parsed.DataResidency.ShouldBe("internal-only");
        parsed.CheapWhenBelowPct.ShouldBe(15);
    }

    [Fact]
    public void Parse_Rejects_Wrong_Version() =>
        Should.Throw<RoutingRulesException>(() =>
            RoutingRulesDocument.Parse(
                """{"version":"v0","rules":{}}"""))
            .Message.ShouldContain("version");

    [Fact]
    public void Parse_Rejects_Unknown_Role() =>
        Should.Throw<RoutingRulesException>(() =>
            RoutingRulesDocument.Parse(
                """{"version":"routing_rules_v1","rules":{"bogus":[{"model":"m"}]}}"""))
            .Message.ShouldContain("bogus");

    [Fact]
    public void Parse_Rejects_Negative_Weight() =>
        Should.Throw<RoutingRulesException>(() =>
            RoutingRulesDocument.Parse(
                """{"version":"routing_rules_v1","rules":{"code":[{"model":"m","weight":-1}]}}"""))
            .Message.ShouldContain("weight");

    [Fact]
    public void Parse_Rejects_Bad_Budget_Tier() =>
        Should.Throw<RoutingRulesException>(() =>
            RoutingRulesDocument.Parse(
                """{"version":"routing_rules_v1","rules":{"code":[{"model":"m","budget_tier":"gold"}]}}"""))
            .Message.ShouldContain("budget_tier");

    [Fact]
    public void Parse_Rejects_Bad_Residency() =>
        Should.Throw<RoutingRulesException>(() =>
            RoutingRulesDocument.Parse(
                """{"version":"routing_rules_v1","rules":{},"data_residency":"mars"}"""))
            .Message.ShouldContain("data_residency");

    [Fact]
    public void Legacy_Role_Names_Map_To_Canonical() =>
        RoutingRulesDocument.Parse(
            """{"version":"routing_rules_v1","rules":{"worker":[{"model":"m"}]}}""")
            .ShouldNotBeNull(); // "worker" é alias legado de code
}

/// <summary>SPEC-149 RF-001: ordenação de candidatos (núcleo puro).</summary>
public sealed class Spec149RouteEvaluatorTests
{
    private static RoutingRulesDocument Doc(
        string role, params RouteCandidate[] candidates) =>
        new()
        {
            Rules = new Dictionary<string, List<RouteCandidate>>
            {
                [role] = [.. candidates],
            },
        };

    private static RouteEvaluator.Result Eval(
        RoutingRulesDocument doc,
        string role = "code",
        bool pinnable = true,
        string? pin = null,
        double? pct = null,
        int cheapBelow = 20,
        LlmCircuitBreaker? breaker = null,
        int cursor = 0) =>
        RouteEvaluator.Evaluate(
            doc.CandidatesFor(role)!, doc, pinnable, pin, pct,
            cheapBelow, breaker ?? new LlmCircuitBreaker(), cursor);

    [Fact]
    public void First_Healthy_Candidate_Wins_In_Declared_Order()
    {
        var r = Eval(Doc("code", new("a"), new("b"), new("c")));

        r.Ordered.Select(c => c.Model).ShouldBe(["a", "b", "c"]);
        r.Reason.ShouldBe("order");
        r.Evaluated[0].Status.ShouldBe("selected");
        r.Evaluated[1].Status.ShouldBe("eligible");
    }

    [Fact]
    public void Circuit_Open_Candidate_Is_Skipped()
    {
        var breaker = new LlmCircuitBreaker();
        breaker.ReportFailure("p1", "a", TimeSpan.FromMinutes(1));

        var r = Eval(Doc("code", new("a", "p1"), new("b", "p2")),
            breaker: breaker);

        r.Ordered.Select(c => c.Model).ShouldBe(["b"]);
        r.Evaluated[0].Status.ShouldBe("circuit_open");
    }

    [Fact]
    public void Deny_Wins_Over_Everything()
    {
        var doc = Doc("code", new("a"), new("b"));
        doc.DenyModels.Add("a");

        var r = Eval(doc);

        r.Ordered.Select(c => c.Model).ShouldBe(["b"]);
        r.Evaluated[0].Status.ShouldBe("denied");
    }

    [Fact]
    public void Allowlist_Is_A_Hard_Boundary()
    {
        var doc = Doc("code", new("a"), new("b"), new("c"));
        doc.AllowedModels.AddRange(["b", "c"]);

        var r = Eval(doc);

        r.Ordered.Select(c => c.Model).ShouldBe(["b", "c"]);
        r.Evaluated[0].Status.ShouldBe("not_allowed");
    }

    [Fact]
    public void Residency_InternalOnly_Drops_External_Candidates()
    {
        var doc = new RoutingRulesDocument
        {
            Rules = new Dictionary<string, List<RouteCandidate>>
            {
                ["code"] =
                [
                    new RouteCandidate("ext", "openai"),
                    new RouteCandidate("local", "internal-gw",
                        Internal: true),
                ],
            },
            DataResidency = "internal-only",
        };

        var r = Eval(doc);

        r.Ordered.Select(c => c.Model).ShouldBe(["local"]);
        r.Evaluated[0].Status.ShouldBe("residency");
    }

    [Fact]
    public void Low_Budget_Pulls_Cheap_Tier_Forward()
    {
        var doc = Doc("code",
            new("expensive"), new("cheap", BudgetTier: "cheap"));

        var low = Eval(doc, pct: 10);
        var ok = Eval(doc, pct: 80);

        low.Ordered.Select(c => c.Model).ShouldBe(["cheap", "expensive"]);
        low.Reason.ShouldContain("budget_tier");
        ok.Ordered.Select(c => c.Model).ShouldBe(["expensive", "cheap"]);
    }

    [Fact]
    public void Pin_Reorders_Within_Pinnable_Roles_Only()
    {
        var doc = Doc("code", new("a"), new("b"), new("c"));

        Eval(doc, pinnable: true, pin: "c")
            .Ordered[0].Model.ShouldBe("c");
        Eval(doc, pinnable: false, pin: "c") // ex.: role review
            .Ordered[0].Model.ShouldBe("a");
    }

    [Fact]
    public void Pin_Never_Escapes_The_Configured_List()
    {
        var doc = Doc("code", new("a"), new("b"));

        var r = Eval(doc, pin: "rogue-model");
        r.Ordered[0].Model.ShouldBe("a");
        r.Reason.ShouldContain("pin_unlisted");

        doc.DenyModels.Add("a");
        var filtered = Eval(doc, pin: "a");
        filtered.Ordered[0].Model.ShouldBe("b");
        filtered.Reason.ShouldContain("pin_filtered");
    }

    [Fact]
    public void Weight_Picks_Deterministically_By_Cursor()
    {
        var doc = Doc("code",
            new("a", Weight: 1), new("b", Weight: 3));

        Eval(doc, cursor: 0).Ordered[0].Model.ShouldBe("a");  // cursor 0 → a
        Eval(doc, cursor: 1).Ordered[0].Model.ShouldBe("b");  // 1-3 → b
        Eval(doc, cursor: 3).Ordered[0].Model.ShouldBe("b");
        Eval(doc, cursor: 4).Ordered[0].Model.ShouldBe("a");  // wrap
    }

    [Fact]
    public void All_Filtered_Or_Open_Means_No_Route()
    {
        var doc = Doc("code", new("a"), new("b"));
        doc.DenyModels.AddRange(["a", "b"]);

        var r = Eval(doc);

        r.Ordered.ShouldBeEmpty();
        r.Reason.ShouldBe("no_route");
    }
}

/// <summary>SPEC-149 RF-002: circuito open → half-open probe → close/re-open.</summary>
public sealed class Spec149CircuitBreakerTests
{
    [Fact]
    public void Open_Blocks_Until_Cooldown_Then_Half_Opens()
    {
        var now = DateTimeOffset.UtcNow;
        var breaker = new LlmCircuitBreaker(() => now);

        breaker.IsOpen("p", "m").ShouldBeFalse();      // closed
        breaker.ReportFailure("p", "m", TimeSpan.FromSeconds(60));
        breaker.IsOpen("p", "m").ShouldBeTrue();       // open

        now = now.AddSeconds(61);
        breaker.IsOpen("p", "m").ShouldBeFalse();      // half-open (probe)
    }

    [Fact]
    public void Probe_Failure_Reopens_And_Success_Closes()
    {
        var now = DateTimeOffset.UtcNow;
        var breaker = new LlmCircuitBreaker(() => now);
        breaker.ReportFailure("p", "m", TimeSpan.FromSeconds(60));

        now = now.AddSeconds(61);
        breaker.IsOpen("p", "m").ShouldBeFalse();      // probe window
        breaker.ReportFailure("p", "m", TimeSpan.FromSeconds(60));
        breaker.IsOpen("p", "m").ShouldBeTrue();       // re-opened

        now = now.AddSeconds(61);
        breaker.ReportSuccess("p", "m");               // probe ok
        breaker.IsOpen("p", "m").ShouldBeFalse();
    }

    [Fact]
    public void Keys_Are_Per_Provider_Model() =>
        new LlmCircuitBreaker()
            .IsOpen("other", "m")
            .ShouldBeFalse();
}

/// <summary>SPEC-149 e2e do router: regras, fallback+auditoria, 503, pass-through.</summary>
public sealed class Spec149ModelRouterTests
{
    private sealed class FakeCatalog : IModelCatalog
    {
        public Task<IReadOnlyList<ModelInfo>> ListAsync(
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>([
                new ModelInfo("a", "p1", 1000),
                new ModelInfo("b", "p2", 2000),
                new ModelInfo("c", "p3", 3000),
                new ModelInfo("gpt-5", "openai", 400000)]);

        public Task<ModelInfo?> GetAsync(string m, CancellationToken ct) =>
            Task.FromResult<ModelInfo?>(
                new ModelInfo(m, $"p-{m}", 1000));

        public ModelInfo? TryGetCached(string m) => new(m, $"p-{m}", 1000);
    }

    private sealed class FakeRules(RoutingRulesDocument? doc)
        : IRoutingRulesService
    {
        public Task<RoutingRulesDocument?> GetAsync(
            Guid? orgId, CancellationToken ct) =>
            Task.FromResult(doc);

        public Task SetAsync(Guid orgId, RoutingRulesDocument? d,
            string? actorUserId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<LlmRouteContext?> ContextForConversationAsync(
            Guid conversationId, CancellationToken ct) =>
            Task.FromResult<LlmRouteContext?>(
                new LlmRouteContext(conversationId));
    }

    private sealed class FakeAudit : IAuditService
    {
        public List<(string Action, object? Metadata)> Records { get; } = [];

        public Task RecordAsync(
            string action, string? actorUserId = null,
            Guid? orgId = null, string? targetType = null,
            string? targetId = null, string? ip = null,
            string? userAgent = null, object? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Records.Add((action, metadata));
            return Task.CompletedTask;
        }

        public Task<(IReadOnlyList<AuditEntry> Items, int Total)>
            QueryAsync(string? userId, string? action,
                DateTimeOffset? from, DateTimeOffset? to,
                int page, int pageSize,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<AuditEntry>, int)>(([], 0));
    }

    private sealed class FakeBudget(long? limit, long consumed)
        : IUsageBudgetService
    {
        public Task<UsageBudget> GetBudgetAsync(
            Guid userId, Guid? orgId, bool orgScope,
            CancellationToken ct) =>
            Task.FromResult(new UsageBudget(
                limit, consumed,
                limit is { } l ? Math.Max(0, l - consumed) : null,
                DateTimeOffset.UtcNow.AddDays(-30),
                DateTimeOffset.UtcNow, []));

        public Task<LlmUsageBreakdown> GetLlmUsageAsync(
            Guid userId, Guid? orgId, bool orgScope,
            CancellationToken ct) =>
            Task.FromResult(new LlmUsageBreakdown(
                "role", "2026-01", []));
    }

    private static IServiceScopeFactory Scope(
        IRoutingRulesService? rules = null, IAuditService? audit = null,
        IUsageBudgetService? budget = null)
    {
        var sc = new ServiceCollection();
        if (rules is not null)
        {
            sc.AddSingleton(rules);
        }

        if (audit is not null)
        {
            sc.AddSingleton<IAuditService>(audit);
        }

        if (budget is not null)
        {
            sc.AddSingleton(budget);
        }

        return sc.BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
    }

    private static ModelRouter Make(
        RoutingRulesDocument? doc = null,
        FakeAudit? audit = null,
        IUsageBudgetService? budget = null,
        LlmCircuitBreaker? breaker = null,
        AgentLlmOptions? opt = null,
        bool withScope = true) =>
        new(new FakeCatalog(),
            Options.Create(opt ?? new AgentLlmOptions
            {
                Model = "gpt-5",
            }),
            withScope
                ? Scope(new FakeRules(doc), audit, budget)
                : null,
            breaker, null,
            NullLogger<ModelRouter>.Instance);

    private static LlmRouteContext Ctx(
        string? preferred = null, Guid? org = null) =>
        new(Guid.NewGuid(), org ?? Guid.NewGuid(), "u1", preferred);

    [Fact]
    public async Task Resolve_Picks_First_Candidate_And_Audits_Decision()
    {
        var audit = new FakeAudit();
        var doc = new RoutingRulesDocument
        {
            Rules = new Dictionary<string, List<RouteCandidate>>
            {
                ["code"] = [new("a", "p1"), new("b", "p2")],
            },
        };
        var router = Make(doc, audit);
        var sel = await router.ResolveAsync(
            LlmRole.Code, Ctx(), CancellationToken.None);

        sel.ShouldNotBeNull();
        sel.Model.ShouldBe("a");
        sel.Provider.ShouldBe("p1");
        sel.Route!.Candidates.Count.ShouldBe(2);
        sel.Route.CircuitOpenSec.ShouldBe(60);

        var rec = audit.Records.ShouldHaveSingleItem();
        rec.Action.ShouldBe("llm.route_decision");
    }

    [Fact]
    public async Task Resolve_Role_Without_Rules_Falls_To_PassThrough()
    {
        var doc = new RoutingRulesDocument
        {
            Rules = new Dictionary<string, List<RouteCandidate>>
            {
                ["review"] = [new("a")],
            },
        };
        var router = Make(doc);
        var sel = await router.ResolveAsync(
            LlmRole.Code, Ctx(), CancellationToken.None);

        sel!.Model.ShouldBe("gpt-5");        // default AgentLlm:Model
        sel.Reason.ShouldBe("pass_through");
    }

    [Fact]
    public async Task PassThrough_Is_Identical_To_Spec23_Behavior()
    {
        var router = Make(withScope: false);
        var sel = await router.ResolveAsync(
            LlmRole.Code, Ctx(preferred: "user-pick"),
            CancellationToken.None);

        sel!.Model.ShouldBe("user-pick");    // preferred > default
        sel.Route.ShouldBeNull();
        sel.Reason.ShouldBe("pass_through");
    }

    [Fact]
    public async Task No_Route_When_All_Candidates_Filtered()
    {
        var doc = new RoutingRulesDocument
        {
            Rules = new Dictionary<string, List<RouteCandidate>>
            {
                ["code"] = [new("a")],
            },
            DenyModels = ["a"],
        };
        var router = Make(doc);

        (await router.ResolveAsync(
            LlmRole.Code, Ctx(), CancellationToken.None))
            .ShouldBeNull();

        // SelectAsync converte em exceção com semântica 503.
        var ex = await Should.ThrowAsync<LlmNoRouteException>(
            () => router.SelectAsync(Guid.NewGuid(), ModelRole.Worker,
                null, CancellationToken.None));
        ex.StatusCode.ShouldBe(503);
    }

    [Fact]
    public async Task Fallback_Opens_Circuit_Audits_And_Walks_Plan()
    {
        var audit = new FakeAudit();
        var breaker = new LlmCircuitBreaker();
        var doc = new RoutingRulesDocument
        {
            Rules = new Dictionary<string, List<RouteCandidate>>
            {
                ["code"] = [new("a", "p1"), new("b", "p2"),
                            new("c", "p3")],
            },
        };
        var router = Make(doc, audit, breaker: breaker);
        var ctx = Ctx();

        var first = await router.ResolveAsync(
            LlmRole.Code, ctx, CancellationToken.None);
        first!.Model.ShouldBe("a");

        var second = await router.NextFallbackAsync(
            first, LlmErrorKind.RateLimited, CancellationToken.None);
        second!.Model.ShouldBe("b");
        second.FallbackFrom.ShouldBe("a");
        breaker.IsOpen("p1", "a").ShouldBeTrue();

        var fallback = audit.Records
            .Where(r => r.Action == "llm.route_fallback").ToList();
        fallback.Count.ShouldBe(1);

        // Próxima resolução pula o circuito aberto.
        var again = await router.ResolveAsync(
            LlmRole.Code, ctx, CancellationToken.None);
        again!.Model.ShouldBe("b");

        var third = await router.NextFallbackAsync(
            second, LlmErrorKind.Transient, CancellationToken.None);
        third!.Model.ShouldBe("c");
        (await router.NextFallbackAsync(
            third, LlmErrorKind.Transient, CancellationToken.None))
            .ShouldBeNull(); // cadeia esgotada ⇒ caller → llm_no_route
    }

    [Fact]
    public async Task Budget_Tier_Reorders_When_Remaining_Is_Low()
    {
        var doc = new RoutingRulesDocument
        {
            Rules = new Dictionary<string, List<RouteCandidate>>
            {
                ["code"] = [new("expensive"),
                            new("cheap", BudgetTier: "cheap")],
            },
        };
        var low = Make(doc,
            budget: new FakeBudget(limit: 1000, consumed: 950));
        var ok = Make(doc,
            budget: new FakeBudget(limit: 1000, consumed: 100));

        (await low.ResolveAsync(LlmRole.Code, Ctx(),
            CancellationToken.None))!.Model.ShouldBe("cheap");
        (await ok.ResolveAsync(LlmRole.Code, Ctx(),
            CancellationToken.None))!.Model.ShouldBe("expensive");
    }

    [Fact]
    public async Task User_Pin_Applies_To_Code_But_Not_Review()
    {
        var doc = new RoutingRulesDocument
        {
            Rules = new Dictionary<string, List<RouteCandidate>>
            {
                ["code"] = [new("a"), new("b")],
                ["review"] = [new("a"), new("b")],
            },
        };
        var router = Make(doc);

        (await router.ResolveAsync(LlmRole.Code, Ctx(preferred: "b"),
            CancellationToken.None))!.Model.ShouldBe("b");
        (await router.ResolveAsync(LlmRole.Review, Ctx(preferred: "b"),
            CancellationToken.None))!.Model.ShouldBe("a");
    }

    [Fact]
    public async Task Role_Wire_Names_Propagate_To_Usage_Events()
    {
        // SPEC-149 RF-004: a mesma wire name usada na rota é o que vai
        // para TokenEvent.Role — garantido pelo mapping canônico.
        LlmRoles.ToWire(LlmRoles.FromModelRole(ModelRole.Worker))
            .ShouldBe("code");
        LlmRoles.ToWire(LlmRoles.FromModelRole(ModelRole.Reviewer))
            .ShouldBe("review");
        LlmRoles.ToWire(LlmRoles.FromModelRole(ModelRole.Analysis))
            .ShouldBe("analysis");
        LlmRoles.ToWire(LlmRoles.FromModelRole(ModelRole.Summarizer))
            .ShouldBe("summarizer");
        LlmRoles.ToWire(LlmRoles.FromModelRole(ModelRole.Subagent))
            .ShouldBe("subagent");
    }
}