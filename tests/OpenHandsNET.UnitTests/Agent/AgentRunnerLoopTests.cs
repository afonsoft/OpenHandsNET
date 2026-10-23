using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Agent.SystemPrompt;

using Shouldly;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-09 AC: loop ReAct MockLlm → tool → observation → finish; state machine,
/// confirmation mode, interrupt, max_iterations e streaming deltas.
/// </summary>
public sealed class AgentRunnerLoopTests
{
    private const string FinishScript = """
        [{"tool":{"name":"finish","arguments":{"final_message":"done"}}}]
        """;

    [Fact]
    public async Task Script_With_Finish_Ends_In_Finished_Status()
    {
        await using var fx = new AgentRunnerFixture(FinishScript);
        await fx.Runner.RunAsync(CancellationToken.None);

        var kinds = fx.Events.Events.Select(e => e.EventKind).ToList();
        kinds.ShouldContain("SystemPromptEvent");
        kinds.ShouldContain("ActionEvent");
        kinds.ShouldContain("ObservationEvent");
        kinds.ShouldContain("MessageEvent"); // final_message
        var lastState = fx.Events.Events
            .OfType<ConversationStateUpdateEvent>()
            .Last(e => e.Key == "execution_status");
        lastState.Value!.Value.GetString().ShouldBe("finished");
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }

    [Fact]
    public async Task Bash_Tool_Executes_And_Emits_Observation_With_Output()
    {
        const string script = """
            [{"tool":{"name":"bash","arguments":{"command":"echo hi"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script);
        fx.Executor.ExecHandler = _ => new CommandResult(0, "hi\n", "", false);
        await fx.Runner.RunAsync(CancellationToken.None);

        fx.Executor.Commands.ShouldContain("echo hi");
        var obs = fx.Events.Events.OfType<ObservationEvent>()
            .First(e => e.ToolName == "bash");
        obs.Observation.GetProperty("output").GetString().ShouldBe("hi\n");
        obs.Observation.GetProperty("exit_code").GetInt32().ShouldBe(0);
        var action = fx.Events.Events.OfType<ActionEvent>().First();
        obs.ActionId.ShouldBe(action.Id);
        obs.ParentId.ShouldBe(action.Id);
    }

    [Fact]
    public async Task Text_Deltas_Go_To_Ephemeral_Bus_Not_Event_Store()
    {
        const string script = """
            [{"text":"hello world"},
             {"tool":{"name":"finish","arguments":{"final_message":"fim"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script);
        await fx.Runner.RunAsync(CancellationToken.None);

        // SPEC-34 RF-001: deltas viraram efêmeros (bus) — o stream durável
        // traz só o MessageEvent final com o item_id do slot efêmero.
        fx.Events.Events.ShouldNotContain(
            e => e.GetType() == typeof(StreamingDeltaEvent));
        var agentMsg = fx.Events.Events
            .OfType<MessageEvent>()
            .First(e => e.Source == EventSource.Agent);
        agentMsg.ItemId.ShouldNotBeNullOrEmpty();
        agentMsg.LlmMessage.GetProperty("content")[0]
            .GetProperty("text").GetString()!.ShouldContain("hello world");
        fx.Events.Events.OfType<TokenEvent>().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Confirmation_Mode_Pauses_Risky_Action_Until_Approved()
    {
        const string script = """
            [{"tool":{"name":"bash","arguments":{"command":"rm -rf /tmp/x"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]
            """;
        await using var fx = new AgentRunnerFixture(
            script, confirmationMode: true);
        var run = fx.Runner.RunAsync(CancellationToken.None);

        // Aguarda o estado waiting_for_confirmation (Value é objeto com status).
        await WaitForAsync(() =>
            fx.Events.Events.OfType<ConversationStateUpdateEvent>()
                .LastOrDefault(e => e.Key == "execution_status"
                    && e.Value?.ToString().Contains("waiting") == true));
        var pendingActionId = fx.Events.Events.OfType<ActionEvent>()
            .Single(e => e.ToolName == "bash").Id;

        fx.Executor.Commands.Where(c => c != SddSurfaceProbe.Command)
            .ShouldBeEmpty(); // não executou ainda
        fx.Runner.ResolveConfirmation(pendingActionId, approved: true);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        fx.Executor.Commands.ShouldContain("rm -rf /tmp/x");
    }

    [Fact]
    public async Task Rejected_Action_Skips_Execution_And_Continues()
    {
        const string script = """
            [{"tool":{"name":"bash","arguments":{"command":"danger"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]
            """;
        await using var fx = new AgentRunnerFixture(
            script, confirmationMode: true);
        var run = fx.Runner.RunAsync(CancellationToken.None);

        await WaitForAsync(() =>
            fx.Events.Events.OfType<ConversationStateUpdateEvent>()
                .LastOrDefault(e => e.Key == "execution_status"
                    && e.Value?.ToString().Contains("waiting") == true));
        var actionId = fx.Events.Events.OfType<ActionEvent>().Single().Id;
        fx.Runner.ResolveConfirmation(actionId, approved: false);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        fx.Executor.Commands.Where(c => c != SddSurfaceProbe.Command)
            .ShouldBeEmpty();
        fx.Events.Events.OfType<ObservationEvent>()
            .ShouldNotContain(e => e.ToolName == "bash");
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }

    [Fact]
    public async Task AlwaysAllow_Emits_DecisionAuto_And_Runs_Routine_Risky()
    {
        const string script = """
            [{"tool":{"name":"bash","arguments":{"command":"echo hi"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]
            """;
        await using var fx = new AgentRunnerFixture(
            script, confirmationMode: false);
        await fx.Runner.RunAsync(CancellationToken.None);

        var decision = fx.Events.Events.OfType<DecisionAutoEvent>().Single();
        decision.Chosen.ShouldBe("approve");
        decision.Reason.ShouldBe("always_allow");
        decision.AskedId.ShouldBe(
            fx.Events.Events.OfType<ActionEvent>()
                .Single(e => e.ToolName == "bash").Id);
        fx.Executor.Commands.ShouldContain("echo hi");
        fx.Events.Events.OfType<ConversationStateUpdateEvent>()
            .Count(e => e.Value?.ToString().Contains("waiting") == true)
            .ShouldBe(0);
    }

    [Fact]
    public async Task AlwaysAllow_Destructive_Still_Pauses_For_Human()
    {
        const string script = """
            [{"tool":{"name":"bash","arguments":{"command":"git push --force origin main"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]
            """;
        await using var fx = new AgentRunnerFixture(
            script, confirmationMode: false);
        var run = fx.Runner.RunAsync(CancellationToken.None);

        await WaitForAsync(() =>
            fx.Events.Events.OfType<ConversationStateUpdateEvent>()
                .LastOrDefault(e => e.Key == "execution_status"
                    && e.Value?.ToString().Contains("waiting") == true));
        fx.Events.Events.OfType<DecisionAutoEvent>().ShouldBeEmpty();
        fx.Executor.Commands.Where(c => c != SddSurfaceProbe.Command)
            .ShouldBeEmpty();

        var actionId = fx.Events.Events.OfType<ActionEvent>()
            .Single(e => e.ToolName == "bash").Id;
        fx.Runner.ResolveConfirmation(actionId, approved: true);
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        fx.Executor.Commands.ShouldContain("git push --force origin main");
    }

    [Fact]
    public async Task InterruptEvent_Stops_Loop_At_Iteration_Boundary()
    {
        const string script = """
            [{"tool":{"name":"think","arguments":{"thought":"step1"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"late"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script);
        // Interrupt antes do start: _interruptSeen é volatile e a checagem
        // no topo da iteração 0 encerra o loop antes de qualquer tool call.
        fx.Runner.OnEvent(new InterruptEvent());
        var run = fx.Runner.RunAsync(CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var lastState = fx.Events.Events
            .OfType<ConversationStateUpdateEvent>()
            .Last(e => e.Key == "execution_status");
        lastState.Value!.Value.GetString().ShouldBe("idle");
        fx.Events.Events.OfType<ActionEvent>()
            .ShouldNotContain(e => e.ToolName == "finish");
    }

    [Fact]
    public async Task MaxIterations_Bound_Ends_In_Stuck()
    {
        const string script = """
            [{"text":"a"},{"text":"b"},{"text":"c"},{"text":"d"}]
            """;
        await using var fx = new AgentRunnerFixture(script,
            new AgentLlmOptions { MaxIterations = 2 });
        await fx.Runner.RunAsync(CancellationToken.None);

        var lastState = fx.Events.Events
            .OfType<ConversationStateUpdateEvent>()
            .Last(e => e.Key == "execution_status");
        lastState.Value!.Value.GetString().ShouldBe("stuck");
        (await fx.RecordStatusAsync()).ShouldBe("Error");
    }

    private static async Task<T> WaitForAsync<T>(
        Func<T?> probe, int timeoutMs = 10000) where T : class
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } hit)
            {
                return hit;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("condition not met");
    }
}