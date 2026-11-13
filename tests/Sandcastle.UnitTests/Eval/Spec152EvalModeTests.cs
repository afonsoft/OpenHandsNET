using Sandcastle.Application.Agent;
using Sandcastle.Application.Eval;
using Sandcastle.Domain.Events;
using Sandcastle.UnitTests.Agent;

using Shouldly;

namespace Sandcastle.UnitTests.Eval;

/// <summary>
/// SPEC-152 §5/RF-002 — eval_mode no AgentRunner (agent_type="eval"):
/// postbacks externos (SCM/ChatOps/ticket) nunca disparam, confirmação
/// é auto-aprovada (sem operador), ask_user recebe resposta sintética
/// e Eval:MaxTurns capa o loop. Conversas normais seguem o caminho
/// original (control test).
/// </summary>
public sealed class Spec152EvalModeTests
{
    private const string FinishScript = """
        [{"tool":{"name":"finish","arguments":{"final_message":"done"}}}]
        """;

    private sealed class FakeScmReporter
        : Application.Scm.IScmStatusReporter
    {
        public int Calls;
        public Task ReportRunFinishedAsync(
            Guid conversationId, bool success, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeChatReporter
        : Application.ChatOps.IChatOpsReporter
    {
        public int Calls;
        public Task ReportRunFinishedAsync(
            Guid conversationId, bool success, string? summary,
            CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTicketReporter
        : Application.Tickets.ITicketReporter
    {
        public int Calls;
        public Task ReportRunFinishedAsync(
            Guid conversationId, bool success, string? summary,
            string? pullRequestUrl, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }
    }

    private static void EvalConv(
        Infrastructure.Conversations.ConversationRecord c) =>
        c.AgentType = EvalAgentTypes.Eval;

    private static async Task<int> PollCallsAsync(
        Func<int> calls, int ms = 2000)
    {
        // Postbacks são fire-and-forget — polling até estabilizar.
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline && calls() == 0)
        {
            await Task.Delay(25);
        }

        return calls();
    }

    [Fact]
    public async Task EvalMode_Suppresses_External_Postbacks()
    {
        var scm = new FakeScmReporter();
        var chat = new FakeChatReporter();
        var ticket = new FakeTicketReporter();
        await using var fx = new AgentRunnerFixture(
            FinishScript, configureConv: EvalConv,
            scmReporter: scm, chatOpsReporter: chat,
            ticketReporter: ticket);
        await fx.Runner.RunAsync(CancellationToken.None);

        (await fx.RecordStatusAsync()).ShouldBe("Finished");
        await Task.Delay(300); // janela p/ fire-and-forget
        scm.Calls.ShouldBe(0);
        chat.Calls.ShouldBe(0);
        ticket.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Normal_Conversation_Still_Postbacks()
    {
        var scm = new FakeScmReporter();
        var chat = new FakeChatReporter();
        var ticket = new FakeTicketReporter();
        await using var fx = new AgentRunnerFixture(
            FinishScript, // agent_type null — conversa normal.
            scmReporter: scm, chatOpsReporter: chat,
            ticketReporter: ticket);
        await fx.Runner.RunAsync(CancellationToken.None);

        (await fx.RecordStatusAsync()).ShouldBe("Finished");
        (await PollCallsAsync(() => scm.Calls)).ShouldBe(1);
        (await PollCallsAsync(() => chat.Calls)).ShouldBe(1);
        (await PollCallsAsync(() => ticket.Calls)).ShouldBe(1);
    }

    [Fact]
    public async Task EvalMode_AutoApproves_Confirmations()
    {
        // confirmation_mode=on + bash risky → normalmente trava em
        // waiting_for_confirmation; eval auto-aprova com trilha.
        const string script = """
            [{"tool":{"name":"bash","arguments":{"command":"echo hi"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script,
            confirmationMode: true, configureConv: EvalConv);
        await fx.Runner.RunAsync(CancellationToken.None);

        fx.Executor.Commands.ShouldContain("echo hi");
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
        var kinds = fx.Events.Events.Select(e => e.EventKind).ToList();
        kinds.ShouldNotContain("waiting_for_confirmation");
        fx.Events.Events.OfType<DecisionAutoEvent>()
            .ShouldContain(e => e.Reason == "eval_mode");
    }

    [Fact]
    public async Task EvalMode_AskUser_Gets_Synthetic_Answer()
    {
        const string script = """
            [{"tool":{"name":"ask_user","arguments":{"question":"ok?"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script,
            configureConv: EvalConv);
        await fx.Runner.RunAsync(CancellationToken.None);

        (await fx.RecordStatusAsync()).ShouldBe("Finished");
        var kinds = fx.Events.Events.Select(e => e.EventKind).ToList();
        kinds.ShouldNotContain("waiting_for_question");
        fx.Events.Events.OfType<ObservationEvent>()
            .Select(e => e.Observation.GetRawText())
            .ShouldContain(t => t.Contains("eval mode",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EvalMode_Caps_Loop_At_EvalMaxTurns()
    {
        // Respostas text-only mantêm o loop vivo; MaxTurns=1 capa
        // opt.MaxIterations=50 → erro max_iterations (1).
        const string script = """
            [{"text":"a"},{"text":"b"},{"text":"c"},{"text":"d"}]
            """;
        await using var fx = new AgentRunnerFixture(script,
            options: new AgentLlmOptions { MaxIterations = 50 },
            configureConv: EvalConv,
            evalOptions: new EvalOptions { MaxTurns = 1 });
        await fx.Runner.RunAsync(CancellationToken.None);

        fx.Events.Events.OfType<AgentErrorEvent>()
            .ShouldContain(e =>
                e.Error.Contains("max_iterations (1)"));
        (await fx.RecordStatusAsync()).ShouldBe("Error");
    }
}