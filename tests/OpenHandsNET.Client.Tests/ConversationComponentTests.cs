using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenHandsNET.Client.Components.Conversation;
using OpenHandsNET.Client.Services;

namespace OpenHandsNET.Client.Tests;

/// <summary>SPEC-05 — EventCard/ConfirmationBanner/Planner/State.</summary>
public class ConversationComponentTests : TestContext
{
    public ConversationComponentTests()
    {
        // SPEC-26: EventCard/ChatPane injetam MarkdownService.
        // SPEC-45: ChatPane renderiza ChatComposer (Api/Toasts/JS).
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(new MarkdownService());
        Services.AddSingleton(new ToastService());
        Services.AddSingleton(new OpenHandsNetApiClient(new HttpClient
        {
            BaseAddress = new Uri("http://localhost:8080"),
        }));
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

    [Fact]
    public void EventCard_Renders_User_Message_Right_Aligned()
    {
        var e = Ev("""
            {"id":"e1","kind":"MessageEvent","source":"user",
             "timestamp":"2026-01-01T00:00:00Z",
             "llm_message":{"role":"user",
               "content":[{"type":"text","text":"hello agent"}]}}
            """);
        var cut = RenderComponent<EventCard>(p => p.Add(x => x.Event, e));
        cut.Find(".msg.user").TextContent.ShouldContain("hello agent");
    }

    [Fact]
    public void EventCard_Renders_Bash_Action_With_Command()
    {
        var e = Ev("""
            {"id":"e2","kind":"ActionEvent","source":"agent",
             "timestamp":"2026-01-01T00:00:00Z",
             "tool":{"name":"bash","arguments":{"command":"ls -la /workspace"}}}
            """);
        var cut = RenderComponent<EventCard>(p => p.Add(x => x.Event, e));
        cut.Markup.ShouldContain("bash");
        cut.Markup.ShouldContain("ls -la /workspace");
    }

    [Fact]
    public void EventCard_Renders_Error_As_Alert()
    {
        var e = Ev("""
            {"id":"e3","kind":"ServerErrorEvent","source":"environment",
             "timestamp":"2026-01-01T00:00:00Z","error":"sandbox boom"}
            """);
        var cut = RenderComponent<EventCard>(p => p.Add(x => x.Event, e));
        cut.Find(".ev-card.error").TextContent.ShouldContain("sandbox boom");
    }

    [Fact]
    public void ConfirmationBanner_Invokes_Approve_And_Reject()
    {
        var results = new List<bool>();
        var pending = new ConversationStateService.PendingActionInfo(
            "a1", "bash: rm -rf /");
        var cut = RenderComponent<ConfirmationBanner>(p => p
            .Add(x => x.Pending, pending)
            .Add(x => x.OnConfirm, b => results.Add(b)));

        cut.Find("button.primary").Click();
        cut.Find("button.danger").Click();
        results.ShouldBe([true, false]);
    }

    [Fact]
    public void State_Token_Events_Aggregate_Usage()
    {
        var state = NewState();
        state.Apply(Ev("""
            {"id":"t1","kind":"TokenEvent","source":"agent",
             "timestamp":"2026-01-01T00:00:00Z",
             "prompt_tokens":120,"completion_tokens":30,"cost_usd":0.002}
            """));
        state.PromptTokens.ShouldBe(120);
        state.CompletionTokens.ShouldBe(30);
        state.CostUsd.ShouldBe(0.002);
    }

    [Fact]
    public void State_Status_Update_Sets_Execution_And_Pending()
    {
        var state = NewState();
        state.Apply(Ev("""
            {"id":"s1","kind":"ConversationStateUpdateEvent","source":"agent",
             "timestamp":"2026-01-01T00:00:00Z",
             "value":{"execution_status":"waiting_for_confirmation",
                      "pending_action":{"action_id":"act-9","tool":"bash"}}}
            """));
        state.ExecutionStatus.ShouldBe("WAITING_FOR_CONFIRMATION");
        state.PendingAction.ShouldNotBeNull();
        state.PendingAction!.ActionId.ShouldBe("act-9");
    }

    [Fact]
    public void PlannerPane_Shows_Task_Tracker_Items()
    {
        var state = NewState();
        state.Apply(Ev("""
            {"id":"p1","kind":"ActionEvent","source":"agent",
             "timestamp":"2026-01-01T00:00:00Z",
             "tool":{"name":"task_tracker",
               "arguments":{"tasks":[
                 {"title":"inspect repo","status":"done"},
                 {"title":"write fix","status":"in_progress"},
                 {"title":"run tests","status":"pending"}]}}}
            """));
        var cut = RenderComponent<PlannerPane>(p => p.Add(x => x.State, state));
        cut.Markup.ShouldContain("inspect repo");
        cut.Markup.ShouldContain("write fix");
        cut.FindAll(".planner li.done").Count.ShouldBe(1);
    }

    // ---------- SPEC-34: streaming deltas ----------

    [Fact]
    public void Delta_Slot_Accumulates_Frames_And_Renders()
    {
        var state = NewState();
        state.ApplyDelta(new ConversationHubClient.DeltaItem("item-1", 0, "hel"));
        state.ApplyDelta(new ConversationHubClient.DeltaItem("item-1", 1, "lo "));
        state.ApplyDelta(new ConversationHubClient.DeltaItem("item-1", 2, "world"));
        state.LiveDeltas["item-1"].ShouldBe("hello world");

        var cut = RenderComponent<ChatPane>(p => p.Add(x => x.State, state));
        cut.Find(".ev-card.delta pre").TextContent
            .ShouldContain("hello world");
    }

    [Fact]
    public void Durable_Event_With_ItemId_Supersedes_Delta_Slot()
    {
        var state = NewState();
        state.ApplyDelta(new ConversationHubClient.DeltaItem("item-7", 0, "partial"));
        state.LiveDeltas.ShouldContainKey("item-7");

        // Evento durável carrega o mesmo item_id → slot some.
        state.Apply(Ev("""
            {"id":"m1","kind":"MessageEvent","source":"agent",
             "timestamp":"2026-01-01T00:00:00Z","item_id":"item-7",
             "llm_message":{"role":"assistant",
               "content":[{"type":"text","text":"partial full"}]}}
            """));
        state.LiveDeltas.ShouldBeEmpty();
        state.Events.Count.ShouldBe(1);
    }

    [Fact]
    public void Durable_Event_Without_ItemId_Keeps_Delta_Slot()
    {
        var state = NewState();
        state.ApplyDelta(new ConversationHubClient.DeltaItem("item-9", 0, "streaming"));
        state.Apply(Ev("""
            {"id":"m2","kind":"MessageEvent","source":"user",
             "timestamp":"2026-01-01T00:00:00Z",
             "llm_message":{"role":"user",
               "content":[{"type":"text","text":"hi"}]}}
            """));
        state.LiveDeltas.ShouldContainKey("item-9");
    }
}
