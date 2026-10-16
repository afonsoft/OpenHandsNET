using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-44 RF-001: projeção de attention no runner —
/// <c>waiting_for_confirmation</c> → confirmation, erro → failed,
/// <c>ask_user</c> → question (com resposta via mensagem do usuário) e
/// limpeza quando o run retoma/termina.
/// </summary>
public sealed class Spec44AttentionTests
{
    private static async Task<string?> AttentionOfAsync(
        AgentRunnerFixture fx)
    {
        var db = fx.ScopeServices.GetRequiredService<AppDbContext>();
        return (await db.Conversations.AsNoTracking()
            .SingleAsync(c => c.Id == fx.ConversationId))
            .NeedsAttentionKind;
    }

    private static async Task WaitForAsync(
        Func<object?> probe, int ms = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (probe() is not null)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("probe never produced a value");
    }

    // AC: permission ask projeta kind=confirmation e resolve limpa.
    [Fact]
    public async Task Confirmation_Projects_Then_Clears()
    {
        const string script = """
            [{"tool":{"name":"bash","arguments":{"command":"echo hi"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script,
            confirmationMode: true);
        var run = fx.Runner.RunAsync(CancellationToken.None);

        await WaitForAsync(() =>
            fx.Events.Events.OfType<ConversationStateUpdateEvent>()
                .LastOrDefault(e => e.Key == "execution_status"
                    && e.Value?.ToString().Contains(
                        "waiting_for_confirmation") == true));
        (await AttentionOfAsync(fx)).ShouldBe("confirmation");

        var actionId = fx.Events.Events.OfType<ActionEvent>()
            .Single(e => e.ToolName == "bash").Id;
        fx.Runner.ResolveConfirmation(actionId, approved: true);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        (await AttentionOfAsync(fx)).ShouldBeNull();
    }

    // AC: run que termina em erro projeta kind=failed (mantém até dismiss).
    [Fact]
    public async Task Error_Projects_Failed()
    {
        const string script = """
            [{"text":"a"},{"text":"b"},{"text":"c"},{"text":"d"}]
            """;
        await using var fx = new AgentRunnerFixture(script,
            new AgentLlmOptions { MaxIterations = 2 });
        await fx.Runner.RunAsync(CancellationToken.None);

        (await fx.RecordStatusAsync()).ShouldBe("Error");
        (await AttentionOfAsync(fx)).ShouldBe("failed");
    }

    // AC: ask_user → status waiting_for_question + kind=question; a
    // mensagem do usuário vira o tool result e limpa a projeção.
    [Fact]
    public async Task AskUser_Projects_Question_And_Answer_Clears()
    {
        const string script = """
            [{"tool":{"name":"ask_user","arguments":{"question":"proceed?"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"done"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script);
        var run = fx.Runner.RunAsync(CancellationToken.None);

        await WaitForAsync(() =>
            fx.Events.Events.OfType<ConversationStateUpdateEvent>()
                .LastOrDefault(e => e.Key == "execution_status"
                    && e.Value?.ToString().Contains(
                        "waiting_for_question") == true));
        (await AttentionOfAsync(fx)).ShouldBe("question");
        // Sem pending_action — approve/reject banner não aparece.
        fx.Events.Events.OfType<ConversationStateUpdateEvent>()
            .Last(e => e.Key == "execution_status")
            .Value?.ToString().ShouldNotContain("pending_action");

        // Usuário responde pelo composer normal → MessageEvent user.
        await fx.Events.AppendAsync(fx.ConversationId, new MessageEvent
        {
            Source = EventSource.User,
            LlmMessage = System.Text.Json.JsonSerializer.SerializeToElement(
                new
                {
                    role = "user",
                    content = new[] { new { type = "text", text = "yes" } },
                }),
        }, CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        // A resposta virou observation do ask_user e o run terminou limpo.
        fx.Events.Events.OfType<ObservationEvent>()
            .ShouldContain(e => e.ToolName == "ask_user");
        (await AttentionOfAsync(fx)).ShouldBeNull();
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }

    // AC: transição de attention publica tick no bus (invalidação push).
    [Fact]
    public async Task Transition_Publishes_Bus_Tick()
    {
        const string script = """
            [{"tool":{"name":"bash","arguments":{"command":"echo hi"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]
            """;
        var bus = new AttentionBus();
        var cts = new CancellationTokenSource();
        var ticks = 0;
        var reader = Task.Run(async () =>
        {
            await foreach (var _ in bus.SubscribeAsync(cts.Token))
            {
                Interlocked.Increment(ref ticks);
            }
        });
        await Task.Delay(50); // deixa o assinante registrar

        await using var fx = new AgentRunnerFixture(script,
            confirmationMode: true, attentionBus: bus);
        var run = fx.Runner.RunAsync(CancellationToken.None);

        await WaitForAsync(() =>
            fx.Events.Events.OfType<ConversationStateUpdateEvent>()
                .LastOrDefault(e => e.Key == "execution_status"
                    && e.Value?.ToString().Contains("waiting") == true));
        await Task.Delay(100);

        var actionId = fx.Events.Events.OfType<ActionEvent>()
            .Single(e => e.ToolName == "bash").Id;
        fx.Runner.ResolveConfirmation(actionId, approved: true);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        cts.Cancel();
        try { await reader; }
        catch (OperationCanceledException) { }
        // set + clear = ≥2 ticks (transições distintas publicam).
        ticks.ShouldBeGreaterThanOrEqualTo(2);
    }
}
