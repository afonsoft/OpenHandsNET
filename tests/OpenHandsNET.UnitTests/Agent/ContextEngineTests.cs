using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Agent.Context;
using Shouldly;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>SPEC-22: estimativa/janela/calibração do HeuristicContextMeter.</summary>
public sealed class HeuristicContextMeterTests
{
    private static HeuristicContextMeter Meter(AgentLlmOptions? o = null) =>
        new(Options.Create(o ?? new AgentLlmOptions()));

    [Fact]
    public void Estimate_Grows_With_Content_And_ToolCalls()
    {
        var m = Meter();
        var small = new List<LlmMessage> { LlmMessage.User("oi") };
        var big = new List<LlmMessage>
        {
            new("system", new string('x', 4000)),
            LlmMessage.User("pergunta"),
            LlmMessage.AssistantToolCall("p", [
                new LlmToolCall("c1", "bash", new string('y', 4000))]),
            LlmMessage.ToolResult("c1", new string('z', 8000)),
        };
        m.EstimateTokens(small, null)
            .ShouldBeLessThan(m.EstimateTokens(big, null));
        m.EstimateTokens(big, null).ShouldBeGreaterThan(4000);
    }

    [Fact]
    public void ContextWindow_Override_Wins_Over_Default()
    {
        var m = Meter(new AgentLlmOptions
        {
            DefaultContextWindow = 128000,
            ContextWindowOverrides = { ["tiny"] = 4096 },
        });
        m.ContextWindow("tiny").ShouldBe(4096);
        m.ContextWindow("other").ShouldBe(128000);
        m.ContextWindow(null).ShouldBe(128000);
        Meter(new AgentLlmOptions { DefaultContextWindow = null })
            .ContextWindow("x").ShouldBeNull();
    }

    [Fact]
    public void ObserveUsage_Calibrates_Within_Clamp()
    {
        var m = Meter();
        var msgs = new List<LlmMessage> { LlmMessage.User(new string('a', 400)) };
        var before = m.EstimateTokens(msgs, null);
        m.ObserveUsage(promptTokens: before * 2, estimatedTokens: before);
        var after = m.EstimateTokens(msgs, null);
        after.ShouldBeGreaterThan(before);
        after.ShouldBeLessThanOrEqualTo((int)(before * 1.5) + 1);
        m.ObserveUsage(0, before); // degenerate — ignorado
        m.EstimateTokens(msgs, null).ShouldBe(after);
    }
}

/// <summary>SPEC-22 RF-002/003: pruning, summarization, boundary rules.</summary>
public sealed class LlmSummarizingCondenserTests
{
    private static LlmSummarizingCondenser Condenser(
        AgentLlmOptions o, IEnumerable<JsonNode>? summaryScript = null) =>
        new(new HeuristicContextMeter(Options.Create(o)),
            new MockLlmProvider(summaryScript ?? []),
            Options.Create(o),
            NullLogger<LlmSummarizingCondenser>.Instance);

    private static LlmMessage BigTool(string id, int chars) =>
        LlmMessage.ToolResult(id, new string('r', chars));

    private static List<LlmMessage> History(int toolCount, int chars)
    {
        var h = new List<LlmMessage> { new("system", "sys") };
        for (var i = 0; i < toolCount; i++)
        {
            h.Add(LlmMessage.AssistantToolCall(null,
                [new LlmToolCall($"c{i}", "bash", "{}")]));
            h.Add(BigTool($"c{i}", chars));
        }

        return h;
    }

    private static List<string?> Ids(int n) =>
        Enumerable.Range(0, n).Select(i => (string?)$"ev{i}").ToList();

    [Fact]
    public async Task Evaluate_Under_Threshold_Returns_Null()
    {
        var c = Condenser(new AgentLlmOptions());
        (await c.EvaluateAsync(
            [LlmMessage.User("hi")], null, CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Evaluate_Over_Threshold_Reports_Tokens_And_Window()
    {
        var c = Condenser(new AgentLlmOptions
        {
            DefaultContextWindow = 100,
            Compaction = { ThresholdPercent = 0.5 },
        });
        var s = await c.EvaluateAsync(
            [new LlmMessage("system", new string('x', 400))],
            null, CancellationToken.None);
        s.ShouldNotBeNull();
        s.Trigger.ShouldBe(CompactionTrigger.Threshold);
        s.Window.ShouldBe(100);
    }

    [Fact]
    public async Task Prune_Replaces_Old_Big_ToolResults_Keeps_Recent()
    {
        var opt = new AgentLlmOptions
        {
            DefaultContextWindow = 100,
            Compaction =
            {
                Enabled = true,
                ThresholdTokens = 10_000_000, // nunca dispara summary
                KeepRecentToolResults = 2,
                PruneMaxChars = 100,
            },
        };
        var c = Condenser(opt);
        var history = History(5, 1000);
        var ids = Ids(history.Count);
        var res = await c.CondenseAsync(history, ids,
            CompactionTrigger.Manual, null, null, CancellationToken.None);

        // 3 mais antigas podadas (5 - keep 2); conteúdo virou marcador.
        res.PrunedEventIds.Count.ShouldBe(3);
        res.Messages.Count(m =>
            m.Role == "tool" && m.Content!.Contains("truncated")).ShouldBe(3);
        res.Messages.Count(m =>
            m.Role == "tool" && m.Content!.Length == 1000).ShouldBe(2);
        res.Summary.ShouldBeNull(); // só prune — sem summarizer
    }

    [Fact]
    public async Task Summarize_Keeps_System_And_Tail_Never_Orphans_Tool()
    {
        var opt = new AgentLlmOptions
        {
            DefaultContextWindow = 100,
            Compaction =
            {
                ThresholdTokens = 1, // sempre acima
                KeepLastTurns = 2,
                KeepRecentToolResults = 0,
                PruneMaxChars = 10,
            },
        };
        var c = Condenser(opt,
            [JsonNode.Parse("""{"text":"SUMMARY: did stuff"}""")!]);
        var history = History(6, 100);
        var ids = Ids(history.Count);
        var res = await c.CondenseAsync(history, ids,
            CompactionTrigger.Threshold, "m", null, CancellationToken.None);

        res.Compacted.ShouldBeTrue();
        res.Summary!.ShouldContain("SUMMARY: did stuff");
        res.Messages[0].Role.ShouldBe("system");
        res.Messages[0].Content!.ShouldBe("sys");
        res.Messages[1].Role.ShouldBe("system"); // summary
        res.Messages[1].Content!.ShouldContain("compacted");
        res.Messages.Skip(2).First().Role.ShouldNotBe("tool"); // sem órfãos
        res.ForgottenEventIds.ShouldNotBeEmpty();
        res.ResultEventIds.Count.ShouldBe(res.Messages.Count);
    }

    [Fact]
    public async Task Summarizer_Failure_Fails_Open_With_Prune_Only()
    {
        var opt = new AgentLlmOptions
        {
            Compaction =
            {
                ThresholdTokens = 1, KeepLastTurns = 2,
                KeepRecentToolResults = 0, PruneMaxChars = 10,
            },
        };
        var failing = new MockLlmProvider(
            [JsonNode.Parse("""{"text":""}""")!]); // summary vazio → aborta
        var c = new LlmSummarizingCondenser(
            new HeuristicContextMeter(Options.Create(opt)), failing,
            Options.Create(opt),
            NullLogger<LlmSummarizingCondenser>.Instance);
        var res = await c.CondenseAsync(History(4, 100), Ids(9),
            CompactionTrigger.Threshold, null, null, CancellationToken.None);
        res.Summary.ShouldBeNull();
        res.PrunedEventIds.ShouldNotBeEmpty(); // prune ainda aplicado
    }

    [Fact]
    public async Task OnCompacting_Callback_Fires_Before_Result()
    {
        var opt = new AgentLlmOptions
        {
            Compaction = { ThresholdTokens = 1, KeepLastTurns = 2 },
        };
        var c = Condenser(opt,
            [JsonNode.Parse("""{"text":"sum"}""")!]);
        var called = false;
        await c.CondenseAsync(History(4, 100), Ids(9),
            CompactionTrigger.Manual, null,
            s => { called = true; return Task.CompletedTask; },
            CancellationToken.None);
        called.ShouldBeTrue();
    }

    /// <summary>
    /// SPEC-22 e2e: janela minúscula → threshold dispara → CondensationRequest
    /// + Condensation + CondensationSummaryEvent no stream, run continua até
    /// finish; context_state ledger presente.
    /// </summary>
    [Fact]
    public async Task Runner_Compacts_On_Threshold_And_Finishes()
    {
        var pad = new string('p', 400);
        var script = "[{\"text\":\"" + pad + "\"},"
            + "{\"tool\":{\"name\":\"bash\",\"arguments\":{\"command\":\"echo x\"}}},"
            + "{\"tool\":{\"name\":\"finish\",\"arguments\":{\"final_message\":\"ok\"}}}]";
        var opt = new AgentLlmOptions
        {
            DefaultContextWindow = 60,
            Compaction =
            {
                ThresholdPercent = 0.5, KeepLastTurns = 2,
                LedgerEveryIterations = 1,
            },
        };
        var meter = new HeuristicContextMeter(Options.Create(opt));
        var condenser = new LlmSummarizingCondenser(meter,
            new MockLlmProvider(
                [JsonNode.Parse("""{"text":"resumo denso"}""")!]),
            Options.Create(opt),
            NullLogger<LlmSummarizingCondenser>.Instance);

        await using var fx = new AgentRunnerFixture(
            script, opt, condenser: condenser);
        await fx.Runner.RunAsync(CancellationToken.None);

        var kinds = fx.Events.Events.Select(e => e.EventKind).ToList();
        kinds.ShouldContain("CondensationRequest");
        kinds.ShouldContain("Condensation");
        kinds.ShouldContain("CondensationSummaryEvent");
        fx.Events.Events.OfType<ConversationStateUpdateEvent>()
            .ShouldContain(e => e.Key == "context_state");
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
        var cond = fx.Events.Events.OfType<Condensation>().Last();
        cond.LlmResponseId.ShouldBe("mock-1");
        cond.Summary.ShouldBe("resumo denso");
    }

    /// <summary>
    /// Resume pós-compaction: rebuild honra ForgottenEventIds e reintroduz
    /// o summary depois do system prompt.
    /// </summary>
    [Fact]
    public async Task Rebuild_After_Condensation_Reinserts_Summary()
    {
        var opt = new AgentLlmOptions();
        var events = new FakeEventService();
        var sc = new ServiceCollection();
        sc.AddSingleton<IEventService>(events);
        var sp = sc.BuildServiceProvider();
        var builder = new ConversationHistoryBuilder(sp);
        var conv = new OpenHandsNET.Infrastructure.Conversations
            .ConversationRecord
        {
            Id = Guid.NewGuid(), ExecutionStatus = "Running",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var tools = new ToolRegistry(
            new InMemoryTaskTrackerService(
                sp.GetRequiredService<IServiceScopeFactory>()),
            Options.Create(opt));
        var convId = conv.Id;

        await events.AppendAsync(convId, new SystemPromptEvent
        {
            SystemPrompt = System.Text.Json.JsonSerializer
                .SerializeToElement("sys", EventJson.Options),
            Tools = System.Text.Json.JsonSerializer
                .SerializeToElement(Array.Empty<object>(), EventJson.Options),
        }, CancellationToken.None);
        var m1 = await events.AppendAsync(convId, new MessageEvent
        {
            Source = EventSource.User,
            LlmMessage = System.Text.Json.JsonSerializer.SerializeToElement(
                new { role = "user", content = "old question" },
                EventJson.Options),
        }, CancellationToken.None);
        var m2 = await events.AppendAsync(convId, new MessageEvent
        {
            Source = EventSource.Agent,
            LlmMessage = System.Text.Json.JsonSerializer.SerializeToElement(
                new { role = "assistant", content = "old answer" },
                EventJson.Options),
        }, CancellationToken.None);
        var cond = (Condensation)await events.AppendAsync(convId,
            new Condensation
            {
                ForgottenEventIds = [m1.Id, m2.Id],
                Summary = "RESUMO",
                SummaryOffset = 1,
                LlmResponseId = "x",
            }, CancellationToken.None);
        await events.AppendAsync(convId, new MessageEvent
        {
            Source = EventSource.User,
            LlmMessage = System.Text.Json.JsonSerializer.SerializeToElement(
                new { role = "user", content = "new question" },
                EventJson.Options),
        }, CancellationToken.None);

        var rebuilt = await builder.BuildAsync(
            convId, conv, tools, CancellationToken.None);

        rebuilt.Messages.Count.ShouldBe(3);
        rebuilt.Messages[0].Content!.ShouldBe("sys");
        rebuilt.Messages[1].Content!.ShouldContain("RESUMO");
        rebuilt.Messages[2].Content!.ShouldBe("new question");
        rebuilt.EventIds[1].ShouldBe(cond.Id);
    }
}
