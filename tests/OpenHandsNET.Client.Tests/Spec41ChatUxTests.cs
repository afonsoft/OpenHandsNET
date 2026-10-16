using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenHandsNET.Client.Components.Conversation;
using OpenHandsNET.Client.Services;

namespace OpenHandsNET.Client.Tests;

/// <summary>
/// SPEC-41 AC: ThinkingSection auto-expand/auto-collapse/re-expand,
/// canal "reasoning" dos deltas, supersedes limpando ambos os slots,
/// ToolCallCard com status done/pending + reasoning persistido, e
/// heartbeat run_progress que não renderiza card.
/// </summary>
public class Spec41ChatUxTests : TestContext
{
    public Spec41ChatUxTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(new MarkdownService());
    }

    private static ClientEvent Ev(string json) => new()
    {
        Raw = JsonDocument.Parse(json).RootElement.Clone(),
    };

    private static ConversationStateService NewState()
    {
        var api = new OpenHandsNetApiClient(new HttpClient
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });
        var hub = new ConversationHubClient(
            new Uri("http://localhost:8080/hubs/conversations"));
        return new ConversationStateService(api, hub);
    }

    // ---------- ThinkingSection ----------

    [Fact]
    public void Thinking_Streaming_Without_Content_Is_Expanded()
    {
        var cut = RenderComponent<ThinkingSection>(p => p
            .Add(x => x.Reasoning, "hmm…")
            .Add(x => x.Streaming, true)
            .Add(x => x.ContentStarted, false));
        cut.Find(".thinking-body").TextContent.ShouldContain("hmm…");
        cut.Markup.ShouldContain("pensando…");
    }

    [Fact]
    public void Thinking_Auto_Collapses_When_Content_Starts()
    {
        var cut = RenderComponent<ThinkingSection>(p => p
            .Add(x => x.Reasoning, "hmm…")
            .Add(x => x.Streaming, true)
            .Add(x => x.ContentStarted, true));
        cut.FindAll(".thinking-body").ShouldBeEmpty();
    }

    [Fact]
    public void Thinking_Manual_Toggle_Wins_Over_Auto()
    {
        var cut = RenderComponent<ThinkingSection>(p => p
            .Add(x => x.Reasoning, "hmm…")
            .Add(x => x.Streaming, true)
            .Add(x => x.ContentStarted, true));
        // Auto colapsado → clique re-expande mesmo com content iniciado.
        cut.Find(".ev-head").Click();
        cut.Find(".thinking-body").TextContent.ShouldContain("hmm…");
        // E colapsa de novo.
        cut.Find(".ev-head").Click();
        cut.FindAll(".thinking-body").ShouldBeEmpty();
    }

    // ---------- ConversationStateService — canal reasoning ----------

    [Fact]
    public void ApplyDelta_Reasoning_Channel_Goes_To_LiveReasoning()
    {
        var state = NewState();
        state.ApplyDelta(new ConversationHubClient.DeltaItem(
            "item-1", 0, "think ", "reasoning"));
        state.ApplyDelta(new ConversationHubClient.DeltaItem(
            "item-1", 1, "answer", "content"));

        state.LiveReasoning["item-1"].ShouldBe("think ");
        state.LiveDeltas["item-1"].ShouldBe("answer");
    }

    [Fact]
    public void ApplyDelta_Default_Channel_Is_Content()
    {
        var state = NewState();
        state.ApplyDelta(new ConversationHubClient.DeltaItem("i", 0, "x"));
        state.LiveDeltas["i"].ShouldBe("x");
        state.LiveReasoning.ShouldBeEmpty();
    }

    [Fact]
    public void Durable_Event_Supersedes_Both_Channels()
    {
        var state = NewState();
        state.ApplyDelta(new ConversationHubClient.DeltaItem(
            "item-1", 0, "think", "reasoning"));
        state.ApplyDelta(new ConversationHubClient.DeltaItem(
            "item-1", 1, "text", "content"));
        state.Apply(Ev("""
            {"id":"e1","kind":"MessageEvent","source":"agent",
             "item_id":"item-1","timestamp":"2026-01-01T00:00:00Z",
             "llm_message":{"role":"assistant",
               "content":[{"type":"text","text":"text"}]}}
            """));

        state.LiveDeltas.ShouldBeEmpty();
        state.LiveReasoning.ShouldBeEmpty();
    }

    // ---------- EventCard — ToolCallCard rico ----------

    [Fact]
    public void EventCard_Action_Shows_Pending_Without_Observation()
    {
        var e = Ev("""
            {"id":"a1","kind":"ActionEvent","source":"agent",
             "timestamp":"2026-01-01T00:00:00Z",
             "tool":{"name":"file_read","arguments":{"path":"/x"}}}
            """);
        var cut = RenderComponent<EventCard>(p => p
            .Add(x => x.Event, e).Add(x => x.Siblings, [e]));
        cut.Markup.ShouldContain("pending");
    }

    [Fact]
    public void EventCard_Action_Links_Observation_As_Done()
    {
        var action = Ev("""
            {"id":"a1","kind":"ActionEvent","source":"agent",
             "timestamp":"2026-01-01T00:00:00Z",
             "tool":{"name":"file_read","arguments":{"path":"/x"}}}
            """);
        var obs = Ev("""
            {"id":"o1","kind":"ObservationEvent","source":"environment",
             "action_id":"a1","timestamp":"2026-01-01T00:00:02Z",
             "observation":{"output":"file body"}}
            """);
        var cut = RenderComponent<EventCard>(p => p
            .Add(x => x.Event, action)
            .Add(x => x.Siblings, [action, obs]));
        cut.Markup.ShouldContain("done");
        cut.Markup.ShouldContain("2.0s");
    }

    [Fact]
    public void EventCard_Action_Renders_Persisted_Reasoning_Collapsed()
    {
        var e = Ev("""
            {"id":"a1","kind":"ActionEvent","source":"agent",
             "timestamp":"2026-01-01T00:00:00Z",
             "reasoning_content":"thought about files",
             "tool":{"name":"file_read","arguments":{"path":"/x"}}}
            """);
        var cut = RenderComponent<EventCard>(p => p.Add(x => x.Event, e));
        cut.Find(".ev-card.thinking").ShouldNotBeNull();
        // Persistido chega colapsado — re-expansível via clique.
        cut.FindAll(".thinking-body").ShouldBeEmpty();
        cut.Find(".ev-card.thinking .ev-head").Click();
        cut.Find(".thinking-body").TextContent
            .ShouldContain("thought about files");
    }

    [Fact]
    public void EventCard_Run_Progress_Heartbeat_Renders_Nothing()
    {
        var e = Ev("""
            {"id":"h1","kind":"run_progress","source":"agent",
             "timestamp":"2026-01-01T00:00:00Z",
             "phase":"heartbeat","iteration":0,"max_iterations":0}
            """);
        var cut = RenderComponent<EventCard>(p => p.Add(x => x.Event, e));
        cut.FindAll(".ev-card").ShouldBeEmpty();
    }

    [Fact]
    public void EventCard_Pause_And_Interrupt_Render_Lifecycle_Cards()
    {
        var pause = Ev("""
            {"id":"p1","kind":"PauseEvent","source":"user",
             "timestamp":"2026-01-01T00:00:00Z"}
            """);
        RenderComponent<EventCard>(p => p.Add(x => x.Event, pause))
            .Markup.ShouldContain("run paused");
        var intr = Ev("""
            {"id":"i1","kind":"InterruptEvent","source":"user",
             "timestamp":"2026-01-01T00:00:00Z"}
            """);
        RenderComponent<EventCard>(p => p.Add(x => x.Event, intr))
            .Markup.ShouldContain("run interrupted");
    }
}
