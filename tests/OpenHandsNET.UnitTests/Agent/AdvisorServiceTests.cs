using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Agent.Tools;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-125: <see cref="AdvisorService"/> — inerte sem AdvisorModel,
/// parecer quando ativo, fail-open em erro; tool <c>advisor</c> emite
/// <see cref="AdvisorEvent"/> e devolve o parecer na observação.
/// </summary>
public sealed class AdvisorServiceTests
{
    private sealed class FixedLlm(string text, bool throws = false)
        : IAgentLlmProvider
    {
        public string ModelId => "advisor-test";

        public async IAsyncEnumerable<ILlmStreamChunk> ChatStreamAsync(
            IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<ToolSchema> tools, LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            if (throws)
            {
                throw new InvalidOperationException("provider down");
            }
            yield return new TextDelta(text);
        }

        public Task<ModelInfo> GetModelInfoAsync(CancellationToken ct) =>
            Task.FromResult(new ModelInfo("advisor-test", "test"));
    }

    private static AdvisorService Sut(string? model, bool throws = false) =>
        new(new FixedLlm("use grep before editing", throws),
            Options.Create(new AgentLlmOptions { AdvisorModel = model }));

    [Fact]
    public async Task Inert_Without_AdvisorModel()
    {
        var sut = Sut(null);
        sut.Enabled.ShouldBeFalse();
        (await sut.ConsultAsync("ctx", "q", CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Consult_Returns_Verdict_When_Enabled()
    {
        var verdict = await Sut("adv-model").ConsultAsync(
            "ctx", "q", CancellationToken.None);
        verdict.ShouldNotBeNull();
        verdict!.Answer.ShouldContain("grep");
        verdict.Model.ShouldBe("adv-model");
    }

    [Fact]
    public async Task Provider_Failure_Fails_Open()
    {
        (await Sut("m", throws: true).ConsultAsync(
            "ctx", "q", CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Advisor_Tool_Emits_Event_And_Answer()
    {
        var conv = Guid.NewGuid();
        var events = new FakeEventService();
        var sandbox = new Domain.Sandbox.Sandbox(
            Guid.NewGuid(), Guid.Empty, null, "fake",
            SandboxStatus.Running, null, DateTimeOffset.UtcNow, null, null);
        var sc = new ServiceCollection();
        sc.AddSingleton<IAdvisorService>(Sut("adv-model"));
        var ctx = new ToolCallContext(conv, sandbox,
            new FakeSandboxExecutor(), events,
            sc.BuildServiceProvider(), TimeSpan.FromSeconds(30));

        var res = await BuiltinAdvisor.Advisor().Handler(ctx,
            JsonDocument.Parse("""{"question":"safe?"}""").RootElement,
            CancellationToken.None);

        res.IsError.ShouldBeFalse();
        res.Observation.GetRawText().ShouldContain("grep");
        events.Events.OfType<AdvisorEvent>().Single().Model
            .ShouldBe("adv-model");
    }

    [Fact]
    public async Task Advisor_Tool_Disabled_Returns_Error()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IAdvisorService>(Sut(null));
        var sandbox = new Domain.Sandbox.Sandbox(
            Guid.NewGuid(), Guid.Empty, null, "fake",
            SandboxStatus.Running, null, DateTimeOffset.UtcNow, null, null);
        var ctx = new ToolCallContext(Guid.NewGuid(), sandbox,
            new FakeSandboxExecutor(), new FakeEventService(),
            sc.BuildServiceProvider(), TimeSpan.FromSeconds(30));

        var res = await BuiltinAdvisor.Advisor().Handler(ctx,
            JsonDocument.Parse("""{"question":"q"}""").RootElement,
            CancellationToken.None);
        res.IsError.ShouldBeTrue();
    }
}