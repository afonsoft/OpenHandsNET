using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Agent.Subagents;
using Shouldly;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-28 AC: presets filtram o catálogo do filho, task só existe com
/// MaxDepth>=1, run sync retorna resumo, background via task_status/
/// task_result, cap de concorrência, abort propaga.
/// </summary>
public sealed class Spec28SubagentsTests
{
    // ---------- presets / registry ----------

    [Fact]
    public void Explore_Preset_Blocks_Mutations_And_Task()
    {
        var scopes = new ServiceCollection().BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        var inner = new ToolRegistry(
            new InMemoryTaskTrackerService(scopes),
            Options.Create(new AgentLlmOptions()));
        var filtered = new FilterToolRegistry(
            inner, SubagentPresets.Resolve("explore")!);

        filtered.All.ShouldContain(t => t.Schema.Name == "search");
        filtered.All.ShouldContain(t => t.Schema.Name == "file_read");
        filtered.All.ShouldNotContain(t => t.Schema.Name == "file_write");
        filtered.All.ShouldNotContain(t => t.Schema.Name == "task");
        filtered.Find("file_write").ShouldBeNull();
        filtered.Find("task").ShouldBeNull(); // depth cap
        filtered.Find("file_read").ShouldNotBeNull();
    }

    [Fact]
    public void General_Preset_Blocks_Only_Subagent_Tools()
    {
        var allow = SubagentPresets.Resolve("general")!;
        allow("file_write").ShouldBeTrue();
        allow("bash").ShouldBeTrue();
        allow("mcp__x__y").ShouldBeTrue();
        allow("task").ShouldBeFalse();
        allow("task_status").ShouldBeFalse();
        allow("task_result").ShouldBeFalse();
    }

    [Fact]
    public void Task_Tools_Absent_When_MaxDepth_Zero()
    {
        var scopes = new ServiceCollection().BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        var registry = new ToolRegistry(
            new InMemoryTaskTrackerService(scopes),
            Options.Create(new AgentLlmOptions
            {
                Subagents = { MaxDepth = 0 },
            }));
        registry.Find("task").ShouldBeNull();
        registry.Find("task_status").ShouldBeNull();
        registry.All.ShouldNotContain(t => t.Schema.Name == "task");
    }

    [Fact]
    public void Task_Tools_Present_By_Default()
    {
        var scopes = new ServiceCollection().BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        var registry = new ToolRegistry(
            new InMemoryTaskTrackerService(scopes),
            Options.Create(new AgentLlmOptions()));
        registry.Find("task").ShouldNotBeNull();
        registry.Find("task_status").ShouldNotBeNull();
        registry.Find("task_result").ShouldNotBeNull();
    }

    // ---------- host ----------

    [Fact]
    public void Concurrency_Cap_Rejects_Extra_Runs()
    {
        var host = new SubagentHost();
        var conv = Guid.NewGuid();
        var mk = () => new SubagentRun
        {
            Info = new SubagentRunInfo(
                Ulid.NewUlid().ToString(), "explore", "d",
                SubagentStatus.Running, DateTimeOffset.UtcNow),
        };
        host.TryStart(conv, mk(), maxConcurrent: 2).ShouldBeTrue();
        host.TryStart(conv, mk(), maxConcurrent: 2).ShouldBeTrue();
        host.TryStart(conv, mk(), maxConcurrent: 2).ShouldBeFalse();
        // Finalizar libera slot.
        var first = host.List(conv)[0];
        host.Finish(conv, first.TaskId, SubagentStatus.Completed, "ok");
        host.TryStart(conv, mk(), maxConcurrent: 2).ShouldBeTrue();
    }

    // ---------- e2e no runner ----------

    [Fact]
    public async Task Sync_Task_Runs_Child_And_Returns_Summary()
    {
        const string script = """
            [{"tool":{"name":"task","arguments":{"prompt":"inspect repo","subagent_type":"explore","description":"repo scan"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"child found 3 files"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"parent done"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script);
        await fx.Runner.RunAsync(CancellationToken.None);

        // observation do task traz o resumo do filho
        var obs = fx.Events.Events.OfType<ObservationEvent>()
            .Single(e => e.ToolName == "task");
        obs.Observation.GetProperty("result").GetString()!
            .ShouldContain("child found 3 files");
        // eventos do filho namespaced sob a ação task do pai
        fx.Events.Events.OfType<ConversationStateUpdateEvent>()
            .ShouldContain(e => e.Key == "subagent_run"
                && e.Value.HasValue
                && e.Value.Value.ToString().Contains("repo scan"));
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }

    [Fact]
    public async Task Background_Task_Queues_Then_Completes()
    {
        const string script = """
            [{"tool":{"name":"task","arguments":{"prompt":"scan","background":true}}},
             {"tool":{"name":"finish","arguments":{"final_message":"parent done"}}}]
            """;
        // O provider compartilha a fila entre pai e filho — usar um mock
        // que dá ao subagent uma resposta determinística ("bg result")
        // independente da ordem de dequeue.
        await using var fx = new AgentRunnerFixture(script,
            provider: new SubagentAwareMockProvider(script, "bg result"));
        var run = fx.Runner.RunAsync(CancellationToken.None);

        // espera o task ser observado para capturar o task_id
        var deadline = DateTime.UtcNow.AddSeconds(10);
        ObservationEvent? obs = null;
        while (obs is null && DateTime.UtcNow < deadline)
        {
            obs = fx.Events.Events.OfType<ObservationEvent>()
                .FirstOrDefault(e => e.ToolName == "task");
            if (obs is null)
            {
                await Task.Delay(25);
            }
        }

        obs.ShouldNotBeNull();
        obs.Observation.GetProperty("queued").GetBoolean().ShouldBeTrue();
        var taskId = obs.Observation.GetProperty("task_id").GetString()!;
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        // o run filho completa em background
        var host = fx.ScopeServices.GetRequiredService<ISubagentHost>();
        var child = host.Get(fx.ConversationId, taskId)!;
        await child.Completion!.WaitAsync(TimeSpan.FromSeconds(15));
        host.Get(fx.ConversationId, taskId)!.Info.Status
            .ShouldBe(SubagentStatus.Completed);
        host.Get(fx.ConversationId, taskId)!.Info.Result!
            .ShouldContain("bg result");
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }

    /// <summary>
    /// Mock que brancha pelo system prompt: chamadas cujo system prompt é o
    /// do subagent ("subagent inside OpenHandsNET") recebem um finish fixo;
    /// o resto consome o script do pai em ordem.
    /// </summary>
    private sealed class SubagentAwareMockProvider : IAgentLlmProvider
    {
        private readonly MockLlmProvider _parent;
        private readonly MockLlmProvider _child;

        public SubagentAwareMockProvider(string parentScript, string childResult)
        {
            _parent = new MockLlmProvider(
                System.Text.Json.Nodes.JsonNode.Parse(parentScript)!
                    .AsArray().Select(n => n!).ToList());
            _child = new MockLlmProvider([
                new System.Text.Json.Nodes.JsonObject
                {
                    ["tool"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["name"] = "finish",
                        ["arguments"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["final_message"] = childResult,
                        },
                    },
                }]);
        }

        public string ModelId => _parent.ModelId;

        public Task<ModelInfo> GetModelInfoAsync(CancellationToken ct) =>
            _parent.GetModelInfoAsync(ct);

        public IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            IReadOnlyList<LlmMessage> messages, IReadOnlyList<ToolSchema> tools,
            LlmOptions options, CancellationToken ct) =>
            messages.Count > 0 && messages[0].Role == "system"
                && messages[0].Content?.Contains(
                    "subagent inside OpenHandsNET") == true
                ? _child.ChatStreamAsync(messages, tools, options, ct)
                : _parent.ChatStreamAsync(messages, tools, options, ct);
    }

    [Fact]
    public async Task Unknown_Subagent_Type_Returns_Error()
    {
        const string script = """
            [{"tool":{"name":"task","arguments":{"prompt":"x","subagent_type":"hacker"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"done"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script);
        await fx.Runner.RunAsync(CancellationToken.None);

        var obs = fx.Events.Events.OfType<ObservationEvent>()
            .Single(e => e.ToolName == "task");
        obs.Observation.GetProperty("error").GetString()!
            .ShouldContain("unknown subagent_type");
    }

    [Fact]
    public async Task Explore_Child_Cannot_Call_File_Write()
    {
        const string script = """
            [{"tool":{"name":"task","arguments":{"prompt":"mutate","subagent_type":"explore"}}},
             {"tool":{"name":"file_write","arguments":{"path":"/x","content":"y"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"blocked"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"parent done"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script);
        await fx.Runner.RunAsync(CancellationToken.None);

        var obs = fx.Events.Events.OfType<ObservationEvent>()
            .Single(e => e.ToolName == "file_write");
        obs.Observation.GetProperty("error").GetString()!
            .ShouldContain("not available");
    }
}
