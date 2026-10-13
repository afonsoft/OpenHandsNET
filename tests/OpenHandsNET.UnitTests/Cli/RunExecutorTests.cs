using System.Net;
using System.Text;
using System.Text.Json;
using OpenHandsNET.Cli.Services;

namespace OpenHandsNET.UnitTests.Cli;

/// <summary>
/// RunExecutor: polling do start-task e exit codes do watch
/// (SPEC-06 §10: 0 finished · 2 error · 3 timeout).
/// </summary>
public sealed class RunExecutorTests
{
    private static RunExecutor NewExecutor(
        Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new CloudApiClient(
            new HttpClient(new StubHandler(handler)),
            "http://stub"));

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> fn)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(fn(request));
    }

    private static HttpResponseMessage Json(object payload) => new(
        HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Route(
        HttpRequestMessage req,
        Func<string, object> taskResponse,
        Func<string, object> convResponse,
        Func<string, object> eventsResponse)
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path.Contains("/start-tasks/"))
        {
            return Json(taskResponse(path));
        }

        if (path.Contains("/events"))
        {
            return Json(eventsResponse(path));
        }

        return Json(convResponse(path));
    }

    [Fact]
    public async Task WaitReady_Returns_Conversation_Id_On_Ready()
    {
        var exec = NewExecutor(req => Route(req,
            _ => new
            {
                status = "READY",
                app_conversation_id = "11111111-1111-4111-8111-111111111111",
            },
            _ => new { },
            _ => new { items = Array.Empty<object>() }));

        var (id, err) = await exec.WaitReadyAsync("task-1");
        err.ShouldBeNull();
        id.ShouldBe("11111111-1111-4111-8111-111111111111");
    }

    [Fact]
    public async Task WaitReady_Returns_Error_On_Task_Error()
    {
        var exec = NewExecutor(req => Route(req,
            _ => new { status = "ERROR", detail = "docker boom" },
            _ => new { },
            _ => new { items = Array.Empty<object>() }));

        var (id, err) = await exec.WaitReadyAsync("task-1");
        id.ShouldBeNull();
        err.ShouldBe("docker boom");
    }

    [Fact]
    public async Task Watch_Exit_0_On_Finished()
    {
        var exec = NewExecutor(req => Route(req,
            _ => new { },
            _ => new { execution_status = "FINISHED" },
            _ => new { items = Array.Empty<object>() }));

        var r = await exec.WatchAsync("conv-1", TimeSpan.FromSeconds(5));
        r.ExitCode.ShouldBe(0);
        r.ExecutionStatus.ShouldBe("FINISHED");
    }

    [Fact]
    public async Task Watch_Exit_2_On_Agent_Error()
    {
        var exec = NewExecutor(req => Route(req,
            _ => new { },
            _ => new { execution_status = "ERROR" },
            _ => new { items = Array.Empty<object>() }));

        var r = await exec.WatchAsync("conv-1", TimeSpan.FromSeconds(5));
        r.ExitCode.ShouldBe(2);
    }

    [Fact]
    public async Task Watch_Exit_3_On_Timeout()
    {
        var exec = NewExecutor(req => Route(req,
            _ => new { },
            _ => new { execution_status = "RUNNING" },
            _ => new { items = Array.Empty<object>() }));

        var r = await exec.WatchAsync(
            "conv-1", TimeSpan.FromMilliseconds(100));
        r.ExitCode.ShouldBe(3);
        r.ExecutionStatus.ShouldBe("TIMEOUT");
    }

    [Fact]
    public async Task Watch_Invokes_OnEvent_For_Each_New_Event()
    {
        var calls = 0;
        var exec = NewExecutor(req => Route(req,
            _ => new { },
            _ => new { execution_status = "FINISHED" },
            _ => new
            {
                items = new object[]
                {
                    new { id = "e1", kind = "MessageEvent" },
                    new { id = "e2", kind = "ActionEvent" },
                },
            }));

        var r = await exec.WatchAsync(
            "conv-1", TimeSpan.FromSeconds(5), _ => calls++);
        r.ExitCode.ShouldBe(0);
        calls.ShouldBe(2);
    }

    [Fact]
    public void ToJson_Emits_SnakeCase_One_Line()
    {
        var json = RunExecutor.ToJson(new RunExecutor.Result(
            0, "c1", "FINISHED", null));
        json.ShouldBe(
            "{\"conversation_id\":\"c1\",\"execution_status\":\"FINISHED\","
            + "\"exit_code\":0,\"detail\":null}");
    }
}
