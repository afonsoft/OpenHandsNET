using System.Net;
using System.Text;
using System.Text.Json;

using Sandcastle.Mcp;

namespace Sandcastle.UnitTests.Mcp;

/// <summary>
/// SPEC-129 RF-002/003: E2E do servidor MCP sobre HTTP real — mock da
/// API via <see cref="HttpListener"/> em porta livre, handshake
/// initialize→tools/list→tools/call e caminho de erro (401/down)
/// falhando fechado com <c>isError</c>.
/// </summary>
public sealed class McpEndToEndTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, (int Status, string Body)> _routes = new();

    public McpEndToEndTests()
    {
        var port = FreePort();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        BaseUrl = $"http://localhost:{port}";
        _ = Task.Run(LoopAsync);
    }

    private string BaseUrl { get; }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(
            IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }
            var path = ctx.Request.Url!.AbsolutePath.TrimStart('/');
            var (status, body) = _routes.TryGetValue(path, out var route)
                ? route
                : (404, """{"error":"not found"}""");
            ctx.Response.StatusCode = status;
            var bytes = Encoding.UTF8.GetBytes(body);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private ScApiClient Api(bool withKey = true) =>
        new(new HttpClient(), BaseUrl, withKey ? "sk-test" : null);

    private static async Task<JsonDocument> CallAsync(
        McpServer server, ScApiClient api, string name,
        object? arguments = null)
    {
        var line = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 7,
            method = "tools/call",
            @params = new { name, arguments = arguments ?? new { } },
        });
        var resp = await server.HandleLineAsync(
            line, api, CancellationToken.None);
        resp.ShouldNotBeNull();
        return JsonDocument.Parse(resp!);
    }

    [Fact]
    public async Task Handshake_ToolsList_ToolsCall_HappyPath()
    {
        _routes["api/v1/app-conversations/"] = (200,
            """{"items":[{"id":"c1","title":"demo","status":"READY"}]}""");
        var server = new McpServer();
        var api = Api();

        var init = await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize"}""",
            api, CancellationToken.None);
        JsonDocument.Parse(init!).RootElement.GetProperty("result")
            .GetProperty("protocolVersion").GetString()
            .ShouldBe("2024-11-05");

        var list = await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            api, CancellationToken.None);
        JsonDocument.Parse(list!).RootElement.GetProperty("result")
            .GetProperty("tools").GetArrayLength().ShouldBe(15);

        using var call = await CallAsync(
            server, api, "sc_list_conversations");
        call.RootElement.GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!
            .ShouldContain("demo");
    }

    [Fact]
    public async Task ToolsCall_Api401_ReturnsIsError()
    {
        _routes["api/v1/skills"] = (401, """{"error":"unauthorized"}""");
        using var resp = await CallAsync(
            new McpServer(), Api(), "sc_list_skills");
        var result = resp.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString()!
            .ShouldContain("401");
    }

    [Fact]
    public void Ctor_BaseAddress_Normalizes_Trailing_Slash()
    {
        // SPEC-106: BaseAddress sempre termina em "/" exatamente uma
        // vez, com ou sem slash de entrada (path delimiter via char).
        var a = new ScApiClient(new HttpClient(), "http://h:1", null);
        var b = new ScApiClient(new HttpClient(), "http://h:1/", null);
        a.HasCredentials.ShouldBeFalse();
        b.HasCredentials.ShouldBeFalse();
        var field = typeof(ScApiClient).GetField(
            "_http", System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance)!;
        var httpA = (HttpClient)field.GetValue(a)!;
        var httpB = (HttpClient)field.GetValue(b)!;
        httpA.BaseAddress!.ToString().ShouldBe("http://h:1/");
        httpB.BaseAddress!.ToString().ShouldBe("http://h:1/");
    }

    [Fact]
    public async Task ToolsCall_ApiDown_ReturnsIsError()
    {
        var deadApi = new ScApiClient(
            new HttpClient { Timeout = TimeSpan.FromSeconds(5) },
            "http://localhost:9", "sk-test");
        using var resp = await CallAsync(
            new McpServer(), deadApi, "sc_list_models");
        var result = resp.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        _cts.Dispose();
    }
}