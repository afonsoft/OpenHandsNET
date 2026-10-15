using System.Net;
using System.Net.Http;
using System.Text;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Agent.Tools;
using OpenHandsNET.Infrastructure.Sandbox;
using Shouldly;
using Xunit;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-24: debounce de command_output, fallback agregado do
/// ExecStreamingAsync, parsing SSE do transport e trackers do runner.
/// </summary>
public sealed class ActivityProtocolTests
{
    [Fact]
    public async Task Debouncer_Coalesces_Small_Chunks()
    {
        var flushed = new List<(string Channel, string Text)>();
        var d = new OutputDebouncer((c, t) =>
        {
            flushed.Add((c, t));
            return Task.CompletedTask;
        });
        await d.AddAsync("stdout", "a");
        await d.AddAsync("stdout", "b");
        await d.AddAsync("stderr", "e");
        await d.FlushAsync();
        flushed.ShouldContain(f => f.Channel == "stdout" && f.Text == "ab");
        flushed.ShouldContain(f => f.Channel == "stderr" && f.Text == "e");
    }

    [Fact]
    public async Task Debouncer_Flushes_On_Size_Bound()
    {
        var flushed = new List<string>();
        var d = new OutputDebouncer((_, t) =>
        {
            flushed.Add(t);
            return Task.CompletedTask;
        });
        await d.AddAsync("stdout", new string('x', 40 * 1024));
        flushed.Count.ShouldBe(1); // 40KB > 32KB → flush imediato
    }

    [Fact]
    public async Task Default_Streaming_Falls_Back_To_Aggregated()
    {
        // ISandboxExecutor.ExecStreamingAsync default → um ExecCompleted só.
        ISandboxExecutor exec = new FakeSandboxExecutor();
        var sandbox = new OpenHandsNET.Domain.Sandbox.Sandbox(
            Guid.NewGuid(), Guid.Empty, null, null,
            OpenHandsNET.Domain.Sandbox.SandboxStatus.Running,
            null, DateTimeOffset.UtcNow, null, null);
        var chunks = new List<ExecChunk>();
        await foreach (var c in exec.ExecStreamingAsync(
            sandbox, "echo hi", TimeSpan.FromSeconds(5),
            CancellationToken.None))
        {
            chunks.Add(c);
        }

        chunks.Count.ShouldBe(1);
        chunks[0].ShouldBeOfType<ExecCompleted>();
    }

    [Fact]
    public async Task Transport_Parses_Sse_Chunks_And_Exit()
    {
        var sse = string.Join('\n',
            "data: {\"channel\":\"stdout\",\"text\":\"hello\\n\"}",
            "",
            "data: {\"channel\":\"stderr\",\"text\":\"warn\\n\"}",
            "",
            "data: {\"type\":\"exit\",\"exit_code\":0,\"std_out\":\"hello\\n\",\"std_err\":\"warn\\n\",\"duration_ms\":12,\"truncated\":false}",
            "",
            "");
        var transport = new SandboxAgentTransport(
            new FakeFactory(sse), new Uri("http://agent"), "k",
            agentVersion: 2);
        var chunks = new List<ExecChunk>();
        await foreach (var c in transport.ExecStreamAsync(
            "echo hi", TimeSpan.FromSeconds(5), CancellationToken.None))
        {
            chunks.Add(c);
        }

        chunks.Count.ShouldBe(3);
        ((OutputChunk)chunks[0]).Channel.ShouldBe("stdout");
        ((OutputChunk)chunks[1]).Channel.ShouldBe("stderr");
        var done = (ExecCompleted)chunks[2];
        done.Result.ExitCode.ShouldBe(0);
        done.Result.StdOut.ShouldBe("hello\n");
        done.Truncated.ShouldBeFalse();
    }

    [Fact]
    public async Task Tracker_Emits_Progress_Summary_And_Diagnostic()
    {
        var events = new FakeEventService();
        var t = new RunActivityTracker();
        var conv = Guid.NewGuid();
        t.NoteToolCall("bash", """{"command":"ls"}""");
        t.NoteToolCall("file_write", """{"path":"a.cs"}""");
        await t.ProgressAsync(events, conv, 0, 50, "iteration",
            CancellationToken.None);
        await t.DiagnosticAsync(events, conv, "warn", "ctx", "hi",
            CancellationToken.None);
        await t.SummaryAsync(events, conv, "completed", "done!",
            CancellationToken.None);

        var progress = events.Events.OfType<RunProgressEvent>().Single();
        progress.Iteration.ShouldBe(1);
        progress.ToolsUsed.ShouldBe(2);
        progress.FilesTouched.ShouldBe(1);
        var summary = events.Events.OfType<WorkSummaryEvent>().Single();
        summary.Summary.ShouldBe("done!");
        summary.CommandsRun.ShouldBe(1);
        summary.FilesChanged.ShouldBe(["a.cs"]);
        events.Events.OfType<AgentDiagnosticEvent>().Single()
            .Code.ShouldBe("ctx");
    }

    /// <summary>IHttpClientFactory fake — um response SSE estático.</summary>
    private sealed class FakeFactory(string body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new StaticHandler(body)) { BaseAddress = new Uri("http://agent") };
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    body, Encoding.UTF8, "text/event-stream"),
            });
    }
}
