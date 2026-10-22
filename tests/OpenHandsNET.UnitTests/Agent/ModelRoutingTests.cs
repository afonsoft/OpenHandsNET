using System.Net;
using System.Text;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Agent.Routing;

using Shouldly;

using Xunit;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>SPEC-23 RF-001: parse do catálogo OpenAI-compat + overrides.</summary>
public sealed class OmniRouteModelCatalogTests
{
    private const string ModelsJson = """
        {"data":[
          {"id":"gpt-5","owned_by":"openai","context_window":400000,
           "pricing":{"prompt":1.25,"completion":10.0}},
          {"id":"claude-opus","provider":"anthropic","context_length":200000},
          {"id":"llama-3.3"}
        ]}
        """;

    private static OmniRouteModelCatalog Make(
        HttpMessageHandler handler, AgentLlmOptions? opt = null)
    {
        var factory = new StubFactory(handler);
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new OmniRouteModelCatalog(factory, cache,
            Options.Create(opt ?? new AgentLlmOptions
            {
                Model = "gpt-5",
                BaseUrl = "http://omniroute",
            }),
            NullLogger<OmniRouteModelCatalog>.Instance);
    }

    [Fact]
    public async Task List_Parses_OmniRoute_Extensions()
    {
        var catalog = Make(new StubHandler(HttpStatusCode.OK, ModelsJson));
        var models = await catalog.ListAsync(CancellationToken.None);

        models.Count.ShouldBe(3);
        var gpt = models[0];
        gpt.ContextWindow.ShouldBe(400000);
        gpt.Provider.ShouldBe("openai");
        gpt.Pricing!.InputPerMillion.ShouldBe(1.25m);
        models[1].ContextWindow.ShouldBe(200000); // context_length alias
        models[2].ContextWindow.ShouldBeNull();
    }

    [Fact]
    public async Task Overrides_Win_Over_Discovery()
    {
        var opt = new AgentLlmOptions
        {
            Model = "gpt-5",
            BaseUrl = "http://omniroute",
            ContextWindowOverrides = { ["gpt-5"] = 999 },
        };
        var catalog = Make(new StubHandler(HttpStatusCode.OK, ModelsJson), opt);
        (await catalog.GetAsync("gpt-5", CancellationToken.None))!
            .ContextWindow.ShouldBe(999);
    }

    [Fact]
    public async Task Failure_Fails_Open_To_Configured_Model()
    {
        var catalog = Make(new StubHandler(
            HttpStatusCode.InternalServerError, "boom"));
        var models = await catalog.ListAsync(CancellationToken.None);
        models.Count.ShouldBe(1);
        models[0].Id.ShouldBe("gpt-5");
        models[0].Provider.ShouldBe("configured");
    }

    [Fact]
    public async Task TryGetCached_Serves_Last_Snapshot()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ModelsJson);
        var catalog = Make(handler);
        catalog.TryGetCached("gpt-5").ShouldBeNull(); // nada antes do List
        await catalog.ListAsync(CancellationToken.None);
        catalog.TryGetCached("gpt-5")!.ContextWindow.ShouldBe(400000);
    }

    private sealed class StubHandler(HttpStatusCode status, string body)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8,
                    "application/json"),
            });
    }

    private sealed class StubFactory(HttpMessageHandler handler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}

/// <summary>SPEC-23 RF-002: seleção por role + fallback chain.</summary>
public sealed class ModelRouterTests
{
    private sealed class FakeCatalog(params ModelInfo[] models) : IModelCatalog
    {
        public Task<IReadOnlyList<ModelInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>(models);

        public Task<ModelInfo?> GetAsync(string modelId, CancellationToken ct) =>
            Task.FromResult(models.FirstOrDefault(m => m.Id == modelId));

        public ModelInfo? TryGetCached(string modelId) =>
            models.FirstOrDefault(m => m.Id == modelId);
    }

    private static ModelRouter Make(AgentLlmOptions opt, params ModelInfo[] ms) =>
        new(new FakeCatalog(ms), Options.Create(opt));

    [Fact]
    public async Task Select_Uses_RoleModels_When_Configured()
    {
        var router = Make(new AgentLlmOptions
        {
            Model = "gpt-5",
            RoleModels = { ["summarizer"] = "gpt-5-mini" },
        }, new ModelInfo("gpt-5-mini", "openai", 32000));

        var worker = await router.SelectAsync(
            Guid.NewGuid(), ModelRole.Worker, null, CancellationToken.None);
        var summarizer = await router.SelectAsync(
            Guid.NewGuid(), ModelRole.Summarizer, null,
            CancellationToken.None);

        worker.Model.ShouldBe("gpt-5");
        summarizer.Model.ShouldBe("gpt-5-mini");
        summarizer.ContextWindow.ShouldBe(32000);
    }

    [Fact]
    public async Task PreferredModel_Wins_Over_Default()
    {
        var router = Make(new AgentLlmOptions { Model = "a" });
        var sel = await router.SelectAsync(Guid.NewGuid(), ModelRole.Worker,
            "user-picked", CancellationToken.None);
        sel.Model.ShouldBe("user-picked");
    }

    [Fact]
    public async Task Fallback_Walks_Chain_Without_Repeating()
    {
        var router = Make(new AgentLlmOptions
        {
            Model = "a",
            FallbackModels = ["b", "c"],
        });
        var first = new ModelSelection("a", null, null, 0, null);
        var second = await router.NextFallbackAsync(
            first, LlmErrorKind.ModelUnavailable, CancellationToken.None);
        second!.Model.ShouldBe("b");
        second.FallbackFrom.ShouldBe("a");
        var third = await router.NextFallbackAsync(
            second, LlmErrorKind.Transient, CancellationToken.None);
        third!.Model.ShouldBe("c");
        (await router.NextFallbackAsync(third, LlmErrorKind.Transient,
            CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Fallback_Skips_When_Failed_Model_Not_In_Chain()
    {
        var router = Make(new AgentLlmOptions
        {
            Model = "a",
            FallbackModels = ["b"],
        });
        var sel = await router.NextFallbackAsync(
            new ModelSelection("x", null, null, 0, null),
            LlmErrorKind.RateLimited, CancellationToken.None);
        sel!.Model.ShouldBe("b");
    }

    [Theory]
    [InlineData(LlmErrorKind.Auth)]
    [InlineData(LlmErrorKind.ContextOverflow)]
    public async Task No_Fallback_For_Fatal_Or_Overflow_Kinds(
        LlmErrorKind kind)
    {
        var router = Make(new AgentLlmOptions
        {
            Model = "a",
            FallbackModels = ["b"],
        });
        (await router.NextFallbackAsync(
            new ModelSelection("a", null, null, 0, null), kind,
            CancellationToken.None)).ShouldBeNull();
    }
}

/// <summary>SPEC-23 RF-003: classificação status/body → LlmErrorKind.</summary>
public sealed class LlmErrorClassificationTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "bad key", LlmErrorKind.Auth)]
    [InlineData(HttpStatusCode.Forbidden, "denied", LlmErrorKind.Auth)]
    [InlineData(HttpStatusCode.TooManyRequests, "", LlmErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, "", LlmErrorKind.Transient)]
    [InlineData(HttpStatusCode.BadGateway, "", LlmErrorKind.Transient)]
    [InlineData(HttpStatusCode.RequestTimeout, "", LlmErrorKind.Transient)]
    [InlineData(HttpStatusCode.NotFound, "model_not_found",
        LlmErrorKind.ModelUnavailable)]
    [InlineData(HttpStatusCode.BadRequest, "unknown thing",
        LlmErrorKind.Unknown)]
    [InlineData(HttpStatusCode.BadRequest, "rate_limit_exceeded",
        LlmErrorKind.RateLimited)]
    public void Classify_Maps_Status_And_Body(
        HttpStatusCode status, string body, LlmErrorKind expected) =>
        OpenAiCompatLlmProvider.Classify(status, body).ShouldBe(expected);
}

/// <summary>SPEC-23 RF-003 e2e: runner faz fallback e termina com sucesso.</summary>
public sealed class RunnerFallbackTests
{
    /// <summary>Falha N vezes com LlmCallException, depois emite finish.</summary>
    private sealed class FailThenFinishProvider(
        int failures, LlmErrorKind kind) : IAgentLlmProvider
    {
        private int _left = failures;
        public List<string?> ModelsSeen { get; } = [];

        public string ModelId => "fail-mock";

        public Task<ModelInfo> GetModelInfoAsync(CancellationToken ct) =>
            Task.FromResult(new ModelInfo(ModelId, "mock"));

        public async IAsyncEnumerable<ILlmStreamChunk> ChatStreamAsync(
            IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<ToolSchema> tools, LlmOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct)
        {
            ModelsSeen.Add(options.Model);
            if (_left-- > 0)
            {
                throw new LlmCallException(kind, $"boom {kind}", 404);
            }

            yield return new ToolCallDelta(0, "c1", "finish",
                "{\"final_message\":\"ok\"}");
            yield return new CompletionEnd(
                new LlmUsage(10, 5, null), "tool_calls", "r-fallback");
            await Task.CompletedTask;
        }
    }

    private sealed class FakeCatalog : IModelCatalog
    {
        public Task<IReadOnlyList<ModelInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>([
                new ModelInfo("primary", "gw", 1000),
                new ModelInfo("backup", "gw", 2000)]);

        public Task<ModelInfo?> GetAsync(string m, CancellationToken ct) =>
            Task.FromResult<ModelInfo?>(new ModelInfo(m, "gw", 1000));

        public ModelInfo? TryGetCached(string m) => new(m, "gw", 1000);
    }

    [Fact]
    public async Task Fallback_On_ModelUnavailable_Finishes_Run()
    {
        var provider = new FailThenFinishProvider(1, LlmErrorKind.ModelUnavailable);
        var opt = new AgentLlmOptions
        {
            Model = "primary",
            FallbackModels = ["backup"],
        };
        await using var fx = new AgentRunnerFixture(
            options: opt, provider: provider, catalog: new FakeCatalog());
        await fx.Runner.RunAsync(CancellationToken.None);

        (await fx.RecordStatusAsync()).ShouldBe("Finished");
        provider.ModelsSeen.ShouldBe(["primary", "backup"]);
        var routes = fx.Events.Events
            .OfType<ConversationStateUpdateEvent>()
            .Where(e => e.Key == "llm_route").ToList();
        routes.Count.ShouldBeGreaterThanOrEqualTo(3); // tentativa + falha + fallback
        routes.ShouldContain(e =>
            e.Value!.Value.GetProperty("model").GetString() == "backup"
            && e.Value!.Value.GetProperty("fallback_from").GetString()
                == "primary");
        routes.Any(e =>
            e.Value!.Value.TryGetProperty("kind", out var k)
            && k.GetString() == "modelunavailable").ShouldBeTrue();
        fx.Events.Events.OfType<TokenEvent>().Last().Model.ShouldBe("backup");
    }

    [Fact]
    public async Task Auth_Error_Is_Fatal_Without_Fallback()
    {
        var provider = new FailThenFinishProvider(1, LlmErrorKind.Auth);
        var opt = new AgentLlmOptions
        {
            Model = "primary",
            FallbackModels = ["backup"],
        };
        await using var fx = new AgentRunnerFixture(
            options: opt, provider: provider, catalog: new FakeCatalog());
        await fx.Runner.RunAsync(CancellationToken.None);

        (await fx.RecordStatusAsync()).ShouldBe("Error");
        provider.ModelsSeen.ShouldBe(["primary"]); // sem retentativa
        fx.Events.Events.OfType<AgentErrorEvent>().ShouldNotBeEmpty();
    }
}