using System.Text.Json;

using Sandcastle.Application.Agent;
using Sandcastle.Domain.Events;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Agent;

/// <summary>
/// SPEC-134 RF-002/RF-006: precedência de resolução no run —
/// key do usuário (match do provider do modelo) → BYOR → global.
/// O ledger <c>llm_route</c> registra <c>key_source</c>.
/// </summary>
public sealed class Spec134ByokRunnerTests
{
    private const string FinishScript = """
        [{"tool":{"name":"finish","arguments":{"final_message":"done"}}}]
        """;

    private sealed class FakeCatalog(params ModelInfo[] models) : IModelCatalog
    {
        public Task<IReadOnlyList<ModelInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>(models);

        public Task<ModelInfo?> GetAsync(string modelId, CancellationToken ct) =>
            Task.FromResult(models.FirstOrDefault(m => m.Id == modelId));

        public ModelInfo? TryGetCached(string modelId) =>
            models.FirstOrDefault(m => m.Id == modelId);
    }

    private sealed class FakeUserLlmKeyService(
        UserLlmKeyCallContext? ctx = null,
        IReadOnlyList<UserLlmKeyInfo>? list = null) : IUserLlmKeyService
    {
        public IReadOnlyList<LlmKeyProviderDescriptor> Providers =>
            LlmKeyProviders.Catalog;

        public string? LastResolveProvider { get; private set; }
        public string? LastResolveUser { get; private set; }
        public int ResolveCalls { get; private set; }

        public Task<IReadOnlyList<UserLlmKeyInfo>> ListAsync(
            string userId, CancellationToken ct) =>
            Task.FromResult(list ?? (IReadOnlyList<UserLlmKeyInfo>)[]);

        public Task<UserLlmKeyInfo> UpsertAsync(
            string userId, string provider, string key, string? baseUrl,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            string userId, string provider, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<UserLlmKeyCallContext?> ResolveAsync(
            string userId, string? modelProvider, CancellationToken ct)
        {
            LastResolveUser = userId;
            LastResolveProvider = modelProvider;
            ResolveCalls++;
            return Task.FromResult(ctx);
        }

        public Task<(bool Ok, string? Detail)> TestAsync(
            string userId, string provider, CancellationToken ct) =>
            Task.FromResult((false, (string?)null));
    }

    private sealed class CapturingLlmProvider : IAgentLlmProvider
    {
        private readonly Queue<System.Text.Json.Nodes.JsonNode> _script;
        public LlmOptions? LastOptions { get; private set; }

        public CapturingLlmProvider(string script) => _script =
            new Queue<System.Text.Json.Nodes.JsonNode>(
                System.Text.Json.Nodes.JsonNode.Parse(script)!.AsArray()
                    .Select(n => n!));

        public string ModelId => "capture-llm";

        public Task<ModelInfo> GetModelInfoAsync(CancellationToken ct) =>
            Task.FromResult(new ModelInfo(ModelId, "capture"));

        public async IAsyncEnumerable<ILlmStreamChunk> ChatStreamAsync(
            IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<ToolSchema> tools,
            LlmOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct)
        {
            LastOptions = options;
            var step = _script.Count > 0 ? _script.Dequeue()
                : System.Text.Json.Nodes.JsonNode.Parse(
                    """{"tool":{"name":"finish","arguments":{"final_message":"done"}}}""")!;
            var call = step["tool"]!;
            yield return new ToolCallDelta(0, "c0",
                call["name"]!.GetValue<string>(),
                call["arguments"]!.ToJsonString());
            yield return new CompletionEnd(
                new LlmUsage(1, 1, 0m), "stop", "cap-1");
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Run_Uses_User_Key_When_Provider_Matches()
    {
        var llm = new CapturingLlmProvider(FinishScript);
        var keys = new FakeUserLlmKeyService(
            ctx: new UserLlmKeyCallContext(
                "openai", "https://api.openai.com/v1", "sk-user-aaaa"));
        await using var fx = new AgentRunnerFixture(FinishScript,
            options: new AgentLlmOptions { Model = "gpt-5", ApiKey = "sk-global" },
            provider: llm,
            catalog: new FakeCatalog(new ModelInfo("gpt-5", "openai")),
            configureConv: c =>
            {
                c.CreatedByUserId = "u1";
                c.LlmModel = "gpt-5";
            },
            userKeys: keys);
        await fx.Runner.RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(15));

        keys.LastResolveUser.ShouldBe("u1");
        keys.LastResolveProvider.ShouldBe("openai");
        llm.LastOptions.ShouldNotBeNull();
        llm.LastOptions!.UserKey!.Key.ShouldBe("sk-user-aaaa");
        llm.LastOptions!.UserKey!.BaseUrl.ShouldBe("https://api.openai.com/v1");
        llm.LastOptions.Byor.ShouldBeNull();

        var route = fx.Events.Events.OfType<ConversationStateUpdateEvent>()
            .First(e => e.Key == "llm_route");
        route.Value!.Value.GetProperty("key_source").GetString().ShouldBe("user");
    }

    [Fact]
    public async Task Run_Falls_Back_To_Global_Without_User_Key()
    {
        var llm = new CapturingLlmProvider(FinishScript);
        var keys = new FakeUserLlmKeyService(); // resolve → null
        await using var fx = new AgentRunnerFixture(FinishScript,
            options: new AgentLlmOptions { Model = "gpt-5", ApiKey = "sk-global" },
            provider: llm,
            catalog: new FakeCatalog(new ModelInfo("gpt-5", "openai")),
            configureConv: c => c.CreatedByUserId = "u2",
            userKeys: keys);
        await fx.Runner.RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(15));

        llm.LastOptions!.UserKey.ShouldBeNull();
        var route = fx.Events.Events.OfType<ConversationStateUpdateEvent>()
            .First(e => e.Key == "llm_route");
        route.Value!.Value.GetProperty("key_source").GetString().ShouldBe("global");
    }

    [Fact]
    public async Task User_Key_Wins_Over_Byor()
    {
        var llm = new CapturingLlmProvider(FinishScript);
        var keys = new FakeUserLlmKeyService(
            ctx: new UserLlmKeyCallContext(
                "openai", "https://api.openai.com/v1", "sk-user-bbbb"));
        await using var fx = new AgentRunnerFixture(FinishScript,
            options: new AgentLlmOptions { Model = "gpt-5" },
            provider: llm,
            catalog: new FakeCatalog(new ModelInfo("gpt-5", "openai")),
            configureConv: c => c.CreatedByUserId = "u1",
            byor: new StubByor(
                ctx: new ByorCallContext("http://litellm", "sk-byor", "kid-1"),
                permitted: ["gpt-5"]),
            userKeys: keys);
        await fx.Runner.RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(15));

        llm.LastOptions!.UserKey!.Key.ShouldBe("sk-user-bbbb");
        llm.LastOptions.Byor.ShouldBeNull(); // user-key precede BYOR
        var route = fx.Events.Events.OfType<ConversationStateUpdateEvent>()
            .First(e => e.Key == "llm_route");
        route.Value!.Value.GetProperty("key_source").GetString().ShouldBe("user");
    }

    private sealed class StubByor(
        ByorCallContext? ctx = null,
        IReadOnlyList<string>? permitted = null) : IByorKeyService
    {
        public bool Enabled => true;
        public Task<ByorKeyInfo?> GetActiveAsync(
            string userId, CancellationToken ct) =>
            Task.FromResult<ByorKeyInfo?>(null);
        public Task<(string Key, ByorKeyInfo Info)> RefreshAsync(
            string userId, string? orgId, decimal? budgetUsd,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> RevokeAsync(string userId, CancellationToken ct) =>
            Task.FromResult(false);
        public Task<ByorCallContext?> ResolveCallContextAsync(
            string userId, CancellationToken ct) => Task.FromResult(ctx);
        public Task<IReadOnlyList<string>> PermittedModelsAsync(
            CancellationToken ct) =>
            Task.FromResult(permitted ?? (IReadOnlyList<string>)[]);
    }
}