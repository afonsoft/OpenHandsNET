using System.Text.Json;
using System.Text.Json.Nodes;

using Sandcastle.Mcp;

namespace Sandcastle.UnitTests.Mcp;

/// <summary>
/// SPEC-124 RF-001/006: dispatch JSON-RPC do servidor MCP stdio —
/// parse error, notification sem resposta, métodos do handshake e
/// mapeamento tools/call → API.
/// </summary>
public sealed class McpServerDispatchTests
{
    private readonly McpServer _sut = new();

    private static ScApiClient Api(bool withKey = true) =>
        new(new HttpClient(), "http://localhost:9",
            withKey ? "sk-test" : null);

    [Fact]
    public async Task ParseError_RetornaMenos32700()
    {
        var resp = await _sut.HandleLineAsync(
            "{not-json", Api(), CancellationToken.None);
        resp.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(resp!);
        doc.RootElement.GetProperty("error")
            .GetProperty("code").GetInt32().ShouldBe(-32700);
    }

    [Fact]
    public async Task Notification_SemId_NaoResponde()
    {
        var resp = await _sut.HandleLineAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            Api(), CancellationToken.None);
        resp.ShouldBeNull();
    }

    [Fact]
    public async Task Initialize_AnunciaProtocoloECapabilities()
    {
        var resp = await _sut.HandleLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize"}""",
            Api(), CancellationToken.None);
        using var doc = JsonDocument.Parse(resp!);
        var r = doc.RootElement.GetProperty("result");
        r.GetProperty("protocolVersion").GetString()
            .ShouldBe(McpServer.ProtocolVersion);
        r.GetProperty("serverInfo").GetProperty("name").GetString()
            .ShouldBe("sandcastle-mcp");
        r.GetProperty("capabilities").GetProperty("tools")
            .ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task ToolsList_ExpoeToolsSc()
    {
        var resp = await _sut.HandleLineAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            Api(), CancellationToken.None);
        using var doc = JsonDocument.Parse(resp!);
        var names = doc.RootElement.GetProperty("result")
            .GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        names.ShouldContain("sc_list_conversations");
        names.ShouldContain("sc_create_conversation");
        names.ShouldContain("sc_send_message");
        names.ShouldContain("sc_get_task_status");
        names.ShouldContain("sc_list_skills");
        names.ShouldAllBe(n => n!.StartsWith("sc_"));
    }

    [Fact]
    public async Task MetodoDesconhecido_RetornaMenos32601()
    {
        var resp = await _sut.HandleLineAsync(
            """{"jsonrpc":"2.0","id":3,"method":"resources/list"}""",
            Api(), CancellationToken.None);
        using var doc = JsonDocument.Parse(resp!);
        doc.RootElement.GetProperty("error")
            .GetProperty("code").GetInt32().ShouldBe(-32601);
    }

    [Fact]
    public async Task ToolsCall_SemApiKey_FalhaFechado()
    {
        var resp = await _sut.HandleLineAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"sc_list_conversations","arguments":{}}}""",
            Api(withKey: false), CancellationToken.None);
        using var doc = JsonDocument.Parse(resp!);
        var r = doc.RootElement.GetProperty("result");
        r.GetProperty("isError").GetBoolean().ShouldBeTrue();
        r.GetProperty("content")[0].GetProperty("text").GetString()!
            .ShouldContain("SC_API_KEY");
    }

    [Fact]
    public async Task ToolsCall_SemName_RetornaMenos32602()
    {
        var resp = await _sut.HandleLineAsync(
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{}}""",
            Api(), CancellationToken.None);
        using var doc = JsonDocument.Parse(resp!);
        doc.RootElement.GetProperty("error")
            .GetProperty("code").GetInt32().ShouldBe(-32602);
    }

    [Fact]
    public async Task ToolsCall_ToolDesconhecida_IsError()
    {
        var resp = await _sut.HandleLineAsync(
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"sc_nope","arguments":{}}}""",
            Api(), CancellationToken.None);
        using var doc = JsonDocument.Parse(resp!);
        var r = doc.RootElement.GetProperty("result");
        r.GetProperty("isError").GetBoolean().ShouldBeTrue();
        r.GetProperty("content")[0].GetProperty("text").GetString()!
            .ShouldContain("sc_nope");
    }
}