using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Infrastructure.Agent.Context;
using Sandcastle.Infrastructure.Agent.Routing;
using Sandcastle.Infrastructure.Agent.Security;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Agent;

/// <summary>
/// #434 — SPEC-149 follow-up: the <c>critic</c> role call sites
/// (<see cref="LlmSecurityAnalyzer"/>, <see cref="SmartPermissionJudge"/>)
/// and the condenser's <c>summarizer</c> bridge now carry the run's org
/// in the <see cref="LlmRouteContext"/>, so org-scoped
/// <c>routing_rules</c> engage there. Org-less ctx keeps the pass-through
/// (global candidates only).
/// </summary>
public sealed class CriticOrgRouteContextTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid OtherOrg = Guid.NewGuid();

    private static RoutingRulesDocument DocFor(
        string role, params RouteCandidate[] candidates) =>
        new()
        {
            Rules = new Dictionary<string, List<RouteCandidate>>
            {
                [role] = [.. candidates],
            },
        };

    /// <summary>
    /// Real <see cref="ModelRouter"/> over an org→document map — mirrors
    /// <see cref="RoutingRulesService.GetAsync"/> semantics (null org ⇒
    /// no document ⇒ pass-through).
    /// </summary>
    private static ModelRouter Router(
        IReadOnlyDictionary<Guid, RoutingRulesDocument> byOrg,
        string defaultModel = "global-default") =>
        new(new FakeCatalog(),
            Options.Create(new AgentLlmOptions { Model = defaultModel }),
            Scope(new OrgRules(byOrg)),
            logger: NullLogger<ModelRouter>.Instance);

    private static IServiceScopeFactory Scope(IRoutingRulesService rules)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton(rules);
        return sc.BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
    }

    private static MemoryCache Cache() => new(new MemoryCacheOptions());

    // ---- critic: LlmSecurityAnalyzer ----------------------------------

    [Fact]
    public async Task Analyzer_critic_resolves_org_scoped_rule()
    {
        var llm = new CapturingLlm("low");
        var router = Router(new Dictionary<Guid, RoutingRulesDocument>
        {
            [Org] = DocFor("critic", new RouteCandidate("org-critic-model")),
        });
        var analyzer = new LlmSecurityAnalyzer(llm, Cache(), router);
        var action = System.Text.Json.JsonDocument
            .Parse("""{"command":"ls"}""").RootElement;

        var risk = await analyzer.AnalyzeAsync("bash", action,
            CancellationToken.None,
            new LlmRouteContext(ConversationId: Guid.NewGuid(),
                OrgId: Org));

        risk.ShouldBe(SecurityRisk.Low);
        llm.Models.ShouldBe(["org-critic-model"]);
    }

    [Fact]
    public async Task Analyzer_critic_without_org_ctx_uses_global_only()
    {
        var llm = new CapturingLlm("low");
        var router = Router(new Dictionary<Guid, RoutingRulesDocument>
        {
            [Org] = DocFor("critic", new RouteCandidate("org-critic-model")),
        });
        var analyzer = new LlmSecurityAnalyzer(llm, Cache(), router);
        var action = System.Text.Json.JsonDocument
            .Parse("""{"command":"ls"}""").RootElement;

        // ctx vazio (org-less) → doc da org nunca é consultado.
        await analyzer.AnalyzeAsync("bash", action,
            CancellationToken.None);
        // ctx de OUTRA org → mesma coisa (action diferente — o cache
        // LRU dedup por sha256(tool+args)).
        var action2 = System.Text.Json.JsonDocument
            .Parse("""{"command":"pwd"}""").RootElement;
        await analyzer.AnalyzeAsync("bash", action2,
            CancellationToken.None,
            new LlmRouteContext(OrgId: OtherOrg));

        llm.Models.ShouldBe(["global-default", "global-default"]);
    }

    [Fact]
    public async Task Judge_critic_resolves_org_scoped_rule()
    {
        var llm = new CapturingLlm("""{"decision":"allow"}""");
        var router = Router(new Dictionary<Guid, RoutingRulesDocument>
        {
            [Org] = DocFor("critic", new RouteCandidate("org-judge-model")),
        });
        var judge = new SmartPermissionJudge(llm, Cache(), router);
        var action = System.Text.Json.JsonDocument
            .Parse("""{"path":"/tmp/x"}""").RootElement;

        var risk = await judge.AnalyzeAsync("file_write", action,
            CancellationToken.None,
            new LlmRouteContext(OrgId: Org));

        risk.ShouldBe(SecurityRisk.Low);
        llm.Models.ShouldBe(["org-judge-model"]);
    }

    // ---- condenser bridge: summarizer ---------------------------------

    [Fact]
    public async Task Condenser_summarizer_resolves_org_scoped_rule()
    {
        var llm = new CapturingLlm("dense summary");
        var router = Router(new Dictionary<Guid, RoutingRulesDocument>
        {
            [Org] = DocFor("summarizer",
                new RouteCandidate("org-summary-model")),
        });
        var opt = new AgentLlmOptions
        {
            DefaultContextWindow = 100,
            Compaction =
            {
                ThresholdTokens = 1, KeepLastTurns = 2,
                KeepRecentToolResults = 0, PruneMaxChars = 10,
            },
        };
        var condenser = new LlmSummarizingCondenser(
            new HeuristicContextMeter(Options.Create(opt)), llm,
            Options.Create(opt),
            NullLogger<LlmSummarizingCondenser>.Instance,
            router);

        var res = await condenser.CondenseAsync(
            History(4, 100), Ids(9), CompactionTrigger.Threshold,
            "conv-model", null, CancellationToken.None,
            new LlmRouteContext(OrgId: Org));

        res.Summary.ShouldBe("dense summary");
        llm.Models.ShouldBe(["org-summary-model"]);
    }

    [Fact]
    public async Task Condenser_summarizer_without_ctx_uses_global_only()
    {
        var llm = new CapturingLlm("dense summary");
        var router = Router(new Dictionary<Guid, RoutingRulesDocument>
        {
            [Org] = DocFor("summarizer",
                new RouteCandidate("org-summary-model")),
        });
        var opt = new AgentLlmOptions
        {
            DefaultContextWindow = 100,
            Compaction =
            {
                ThresholdTokens = 1, KeepLastTurns = 2,
                KeepRecentToolResults = 0, PruneMaxChars = 10,
            },
        };
        var condenser = new LlmSummarizingCondenser(
            new HeuristicContextMeter(Options.Create(opt)), llm,
            Options.Create(opt),
            NullLogger<LlmSummarizingCondenser>.Instance,
            router);

        var res = await condenser.CondenseAsync(
            History(4, 100), Ids(9), CompactionTrigger.Threshold,
            "conv-model", null, CancellationToken.None);

        res.Summary.ShouldBe("dense summary");
        // pass-through: RoleModels["summarizer"] ausente ⇒ preferred.
        llm.Models.ShouldBe(["conv-model"]);
    }

    // ---- helpers -------------------------------------------------------

    private static List<LlmMessage> History(int toolCount, int chars)
    {
        var h = new List<LlmMessage> { new("system", "sys") };
        for (var i = 0; i < toolCount; i++)
        {
            h.Add(LlmMessage.AssistantToolCall(null,
                [new LlmToolCall($"c{i}", "bash", "{}")]));
            h.Add(LlmMessage.ToolResult($"c{i}", new string('r', chars)));
        }

        return h;
    }

    private static List<string?> Ids(int n) =>
        Enumerable.Range(0, n).Select(i => (string?)$"ev{i}").ToList();

    /// <summary>LLM fake: captura o modelo resolvido em cada call.</summary>
    private sealed class CapturingLlm(string text) : IAgentLlmProvider
    {
        public List<string?> Models { get; } = [];

        public string ModelId => "cap-llm";

        public async IAsyncEnumerable<ILlmStreamChunk> ChatStreamAsync(
            IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<ToolSchema> tools,
            LlmOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct)
        {
            Models.Add(options.Model);
            yield return new TextDelta(text);
            await Task.CompletedTask;
            yield return new CompletionEnd(
                new LlmUsage(1, 1, 0m), "stop", "cap-1");
        }

        public Task<ModelInfo> GetModelInfoAsync(CancellationToken ct) =>
            Task.FromResult(new ModelInfo(ModelId, "cap"));
    }

    /// <summary>Rules fake com o mesmo contrato do serviço real (org→doc).</summary>
    private sealed class OrgRules(
        IReadOnlyDictionary<Guid, RoutingRulesDocument> byOrg)
        : IRoutingRulesService
    {
        public Task<RoutingRulesDocument?> GetAsync(
            Guid? orgId, CancellationToken ct) =>
            Task.FromResult(orgId is { } o
                && byOrg.TryGetValue(o, out var doc) ? doc : null);

        public Task SetAsync(Guid orgId, RoutingRulesDocument? doc,
            string? actorUserId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<LlmRouteContext?> ContextForConversationAsync(
            Guid conversationId, CancellationToken ct) =>
            Task.FromResult<LlmRouteContext?>(
                new LlmRouteContext(conversationId));
    }

    private sealed class FakeCatalog : IModelCatalog
    {
        public Task<IReadOnlyList<ModelInfo>> ListAsync(
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>([]);

        public Task<ModelInfo?> GetAsync(string m, CancellationToken ct) =>
            Task.FromResult<ModelInfo?>(
                new ModelInfo(m, $"p-{m}", 1000));

        public ModelInfo? TryGetCached(string m) =>
            new(m, $"p-{m}", 1000);
    }
}