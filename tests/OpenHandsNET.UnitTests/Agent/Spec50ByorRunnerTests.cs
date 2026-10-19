using OpenHandsNET.Application.Agent;
using OpenHandsNET.Domain.Events;

using Shouldly;

using Xunit;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-50 RF-003: enforcement fail-closed no runner — o modelo do run
/// precisa estar na lista <c>permitted</c> do proxy quando mode=byor ou
/// o owner tem virtual key ativa; violação → diagnostic
/// <c>byor_model_rejected</c> + run Error (nunca cai no provider local).
/// </summary>
public sealed class Spec50ByorRunnerTests
{
    private const string FinishScript = """
        [{"tool":{"name":"finish","arguments":{"final_message":"done"}}}]
        """;

    [Fact]
    public async Task Byor_Mode_Rejects_Model_Outside_Permitted()
    {
        await using var fx = new AgentRunnerFixture(FinishScript,
            configureConv: c =>
            {
                c.CreatedByUserId = "u1";
                c.LlmModel = "gpt-4o";
            },
            byor: new FakeByorKeyService(
                ctx: new ByorCallContext("http://litellm", "sk-x", "kid-1"),
                permitted: ["claude-sonnet-4"]),
            proxyOptions: new LlmProxyOptions
            {
                BaseUrl = "http://litellm",
                MasterKey = "mk",
                Mode = "byor",
            });
        await fx.Runner.RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(15));

        fx.Events.Events.OfType<AgentDiagnosticEvent>()
            .ShouldContain(d => d.Code == "byor_model_rejected"
                && d.Severity == "error"
                && d.Message.Contains("gpt-4o"));
        (await fx.RecordStatusAsync()).ShouldBe("Error");
    }

    [Fact]
    public async Task Byor_Mode_Allows_Permitted_Model()
    {
        await using var fx = new AgentRunnerFixture(FinishScript,
            configureConv: c =>
            {
                c.CreatedByUserId = "u1";
                c.LlmModel = "claude-sonnet-4";
            },
            byor: new FakeByorKeyService(
                ctx: new ByorCallContext("http://litellm", "sk-x", "kid-1"),
                permitted: ["claude-sonnet-4"]),
            proxyOptions: new LlmProxyOptions
            {
                BaseUrl = "http://litellm",
                MasterKey = "mk",
                Mode = "byor",
            });
        await fx.Runner.RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(15));

        fx.Events.Events.OfType<AgentDiagnosticEvent>()
            .ShouldNotContain(d => d.Code == "byor_model_rejected");
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }

    [Fact]
    public async Task Hybrid_With_Active_Key_Still_Enforces_Permitted()
    {
        // Hybrid + key ativa: a chamada vai ao proxy — enforcement igual.
        await using var fx = new AgentRunnerFixture(FinishScript,
            configureConv: c =>
            {
                c.CreatedByUserId = "u1";
                c.LlmModel = "local-only-model";
            },
            byor: new FakeByorKeyService(
                ctx: new ByorCallContext("http://litellm", "sk-x", "kid-1"),
                permitted: ["gpt-4o-mini"]),
            proxyOptions: new LlmProxyOptions
            {
                BaseUrl = "http://litellm",
                MasterKey = "mk",
                Mode = "hybrid",
            });
        await fx.Runner.RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(15));

        fx.Events.Events.OfType<AgentDiagnosticEvent>()
            .ShouldContain(d => d.Code == "byor_model_rejected");
        (await fx.RecordStatusAsync()).ShouldBe("Error");
    }

    [Fact]
    public async Task Hybrid_Without_Key_Allows_Local_Model()
    {
        // Hybrid sem key BYOR: catálogo local segue disponível.
        await using var fx = new AgentRunnerFixture(FinishScript,
            configureConv: c =>
            {
                c.CreatedByUserId = "u1";
                c.LlmModel = "local-only-model";
            },
            byor: new FakeByorKeyService(
                ctx: null, permitted: ["gpt-4o-mini"]),
            proxyOptions: new LlmProxyOptions
            {
                BaseUrl = "http://litellm",
                MasterKey = "mk",
                Mode = "hybrid",
            });
        await fx.Runner.RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(15));

        fx.Events.Events.OfType<AgentDiagnosticEvent>()
            .ShouldNotContain(d => d.Code == "byor_model_rejected");
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }

    [Fact]
    public async Task Byor_Mode_Without_Key_Fails_Closed()
    {
        // Strict byor sem virtual key do owner → fail closed (não cai
        // no provider local nem segue sem credencial do proxy).
        await using var fx = new AgentRunnerFixture(FinishScript,
            configureConv: c =>
            {
                c.CreatedByUserId = "u1";
                c.LlmModel = "gpt-4o";
            },
            byor: new FakeByorKeyService(
                ctx: null, permitted: ["gpt-4o"]),
            proxyOptions: new LlmProxyOptions
            {
                BaseUrl = "http://litellm",
                MasterKey = "mk",
                Mode = "byor",
            });
        await fx.Runner.RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(15));

        fx.Events.Events.OfType<AgentDiagnosticEvent>()
            .ShouldContain(d => d.Code == "byor_key_missing"
                && d.Severity == "error");
        (await fx.RecordStatusAsync()).ShouldBe("Error");
    }

    [Fact]
    public async Task Byor_Disabled_Skips_Enforcement()
    {
        await using var fx = new AgentRunnerFixture(FinishScript,
            configureConv: c =>
            {
                c.CreatedByUserId = "u1";
                c.LlmModel = "anything";
            },
            byor: new FakeByorKeyService(
                enabled: false, ctx: null, permitted: ["gpt-4o-mini"]));
        await fx.Runner.RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(15));

        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }

    /// <summary>Fake lifecycle service — só o que o runner consome.</summary>
    private sealed class FakeByorKeyService(
        bool enabled = true,
        ByorCallContext? ctx = null,
        IReadOnlyList<string>? permitted = null) : IByorKeyService
    {
        public bool Enabled { get; } = enabled;

        public Task<ByorKeyInfo?> GetActiveAsync(
            string userId, CancellationToken ct) =>
            Task.FromResult<ByorKeyInfo?>(null);

        public Task<(string Key, ByorKeyInfo Info)> RefreshAsync(
            string userId, string? orgId, decimal? budgetUsd,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> RevokeAsync(string userId, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<ByorCallContext?> ResolveCallContextAsync(
            string userId, CancellationToken ct) =>
            Task.FromResult(ctx);

        public Task<IReadOnlyList<string>> PermittedModelsAsync(
            CancellationToken ct) =>
            Task.FromResult(permitted ?? (IReadOnlyList<string>)[]);
    }
}