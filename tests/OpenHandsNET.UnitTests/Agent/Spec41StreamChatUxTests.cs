using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Agent.Deltas;

using Shouldly;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-41 AC: reasoning normalizado por provider (reasoning_content /
/// reasoning / reasoning_details), canal "reasoning" do DeltaFrame,
/// persistência em ActionEvent.ReasoningContent (nunca no content),
/// heartbeat de run_progress, e runner e2e via MockLlmProvider.
/// </summary>
public sealed class Spec41StreamChatUxTests
{
    // ---------- OpenAiCompatLlmProvider.ParseChunk ----------

    private static OpenAiCompatLlmProvider Provider(string sseBody)
    {
        var handler = new SseHandler(sseBody);
        var factory = new FakeHttpFactory(new HttpClient(handler));
        return new OpenAiCompatLlmProvider(
            factory, Options.Create(new AgentLlmOptions()),
            NullLogger<OpenAiCompatLlmProvider>.Instance);
    }

    private static async Task<List<LlmStreamChunk>> CollectAsync(
        OpenAiCompatLlmProvider p)
    {
        var chunks = new List<LlmStreamChunk>();
        await foreach (var c in p.ChatStreamAsync(
            [LlmMessage.User("hi")], [], new LlmOptions(),
            CancellationToken.None))
        {
            chunks.Add(c);
        }

        return chunks;
    }

    [Fact]
    public async Task Parse_Reasoning_Content_Field_Emits_ReasoningDelta()
    {
        var p = Provider(
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"thinking \"}}]}\n"
            + "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"about it\"}}]}\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n"
            + "data: [DONE]\n");

        var chunks = await CollectAsync(p);
        chunks.OfType<ReasoningDelta>().Select(r => r.Text)
            .ShouldBe(["thinking ", "about it"]);
        chunks.OfType<TextDelta>().Single().Text.ShouldBe("answer");
    }

    [Fact]
    public async Task Parse_Reasoning_And_Reasoning_Details_Fields()
    {
        var p = Provider(
            "data: {\"choices\":[{\"delta\":{\"reasoning\":\"r1\"}}]}\n"
            + "data: {\"choices\":[{\"delta\":{\"reasoning_details\":"
            + "[{\"text\":\"r2\"},{\"text\":\"r3\"},{\"type\":\"x\"}]}}]}\n"
            + "data: [DONE]\n");

        var chunks = await CollectAsync(p);
        chunks.OfType<ReasoningDelta>().Select(r => r.Text)
            .ShouldBe(["r1", "r2", "r3"]);
        chunks.OfType<TextDelta>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Parse_No_Reasoning_Fields_Emits_No_ReasoningDelta()
    {
        var p = Provider(
            "data: {\"choices\":[{\"delta\":{\"content\":\"plain\"}}]}\n"
            + "data: [DONE]\n");

        var chunks = await CollectAsync(p);
        chunks.OfType<ReasoningDelta>().ShouldBeEmpty();
        chunks.OfType<TextDelta>().Single().Text.ShouldBe("plain");
    }

    // ---------- DeltaBatcher channels ----------

    [Fact]
    public void Batcher_Reasoning_Channel_Publishes_Reasoning_Frames()
    {
        var bus = new CollectingBus();
        var b = new DeltaBatcher(Guid.NewGuid(), "item-1", bus);
        b.Add("think ", "reasoning");
        b.Add("text");
        b.Flush();
        bus.Frames.Count.ShouldBe(2);
        bus.Frames.Single(f => f.Channel == "reasoning").Text
            .ShouldBe("think ");
        bus.Frames.Single(f => f.Channel == "content").Text
            .ShouldBe("text");
    }

    [Fact]
    public void Batcher_Default_Channel_Is_Content()
    {
        var bus = new CollectingBus();
        var b = new DeltaBatcher(Guid.NewGuid(), "i", bus);
        b.Add("x");
        b.Flush();
        bus.Frames.Single().Channel.ShouldBe("content");
    }

    // ---------- RunActivityTracker heartbeat ----------

    [Fact]
    public async Task Heartbeat_Appends_Run_Progress_Heartbeat()
    {
        var events = new FakeEventService();
        var conv = Guid.NewGuid();
        var tracker = new RunActivityTracker();
        await tracker.HeartbeatAsync(events, conv, CancellationToken.None);
        var hb = events.Events.OfType<RunProgressEvent>().Single();
        hb.Phase.ShouldBe("heartbeat");
        hb.Iteration.ShouldBe(0);
    }

    // ---------- Runner e2e ----------

    private const string ReasoningScript = """
        [{"reasoning":"hmm let me think","text":"",
          "tool":{"name":"finish","arguments":{"final_message":"done"}}}]
        """;

    [Fact]
    public async Task Runner_Reasoning_Flows_To_Bus_And_ActionEvent()
    {
        var bus = new CollectingBus();
        await using var fx = new AgentRunnerFixture(
            ReasoningScript, activityBus: bus);
        await fx.Runner.RunAsync(CancellationToken.None);

        // Deltas de reasoning viajam no canal próprio, com o item_id do turno.
        var reasoningFrames = bus.Frames
            .Where(f => f.Channel == "reasoning").ToList();
        reasoningFrames.ShouldNotBeEmpty();
        string.Concat(reasoningFrames.Select(f => f.Text))
            .ShouldContain("let me think");

        // Persistido em ActionEvent.ReasoningContent — nunca no content.
        var action = fx.Events.Events.OfType<ActionEvent>()
            .FirstOrDefault(a => a.ToolName == "finish");
        action.ShouldNotBeNull();
        action.ReasoningContent.ShouldNotBeNull()
            .ShouldContain("let me think");
    }

    [Fact]
    public async Task Runner_Without_Reasoning_Leaves_ReasoningContent_Null()
    {
        var bus = new CollectingBus();
        await using var fx = new AgentRunnerFixture(
            """
            [{"text":"hi"},
             {"tool":{"name":"finish","arguments":{"final_message":"done"}}}]
            """,
            activityBus: bus);
        await fx.Runner.RunAsync(CancellationToken.None);

        bus.Frames.ShouldAllBe(f => f.Channel == "content");
        fx.Events.Events.OfType<ActionEvent>()
            .ShouldAllBe(a => a.ReasoningContent == null);
    }

    // ---------- fakes ----------

    private sealed class SseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8,
                    "text/event-stream"),
            });
    }

    private sealed class FakeHttpFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CollectingBus : IAgentActivityBus
    {
        public List<DeltaFrame> Frames { get; } = [];

        public void Publish(Guid conversationId, DeltaFrame frame) =>
            Frames.Add(frame);

        public IAsyncEnumerable<DeltaFrame> SubscribeAsync(
            Guid conversationId, CancellationToken ct) =>
            AsyncEnumerable.Empty<DeltaFrame>();
    }
}