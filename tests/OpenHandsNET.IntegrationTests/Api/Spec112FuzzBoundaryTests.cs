using System.Net;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-112 (issue #277): segunda leva de achados do schemathesis fuzz —
/// inputs malformados/tipos errados devem dar 4xx, não 500; 503/502
/// legítimos (dependência não configurada) ficam documentados no OpenAPI.
/// </summary>
public sealed class Spec112FuzzBoundaryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-fuzz2-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec112FuzzBoundaryTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Database:Provider", "Sqlite");
                b.UseSetting(
                    "Database:ConnectionString", $"Data Source={_dbPath};Default Timeout=10");
                b.UseSetting("Observability:OtlpEndpoint", string.Empty);
                b.UseSetting("Auth:Provider", "none");
            });
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static HttpContent Json(string body) => new StringContent(
        body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task DevLogin_Missing_Email_Is_400_Not_500()
    {
        using var http = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        var res = await http.PostAsync("/api/v1/auth/dev-login",
            Json("{\"display_name\": \"x\"}"));
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task EdgeCheck_Wrong_Json_Types_Does_Not_500()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.PostAsync("/api/v1/edge/check",
            Json("{\"ip\": 12345, \"message\": {\"x\": 1}}"));
        ((int)res.StatusCode).ShouldBeLessThan(500);
    }

    [Fact]
    public async Task IpRules_NonString_Cidr_Is_4xx_Not_500()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.PostAsync("/api/v1/ip-rules",
            Json("{\"cidr\": 42, \"priority\": \"high\"}"));
        ((int)res.StatusCode).ShouldBeLessThan(500);
    }

    [Fact]
    public async Task McpToolCall_Empty_Server_Is_400()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.PostAsJsonAsync("/api/v1/mcp/tool-call", new
        {
            conversation_id = Guid.NewGuid(),
            server = "",
            tool = "t",
            arguments = new { },
        });
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task McpToolCall_Unknown_Server_Is_400_Not_502()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.PostAsJsonAsync("/api/v1/mcp/tool-call", new
        {
            conversation_id = Guid.NewGuid(),
            server = "no-such-server",
            tool = "t",
            arguments = new { },
        });
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
    }

    [Fact]
    public async Task WebhookEvent_Invalid_Payload_Is_400()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.PostAsync(
            $"/api/v1/webhooks/events/{Guid.NewGuid()}",
            Json("{\"event\": \"not-an-object\"}"));
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WebhookEvent_Unknown_Conversation_Is_404()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.PostAsync(
            $"/api/v1/webhooks/events/{Guid.NewGuid()}",
            Json("{\"event\": {\"kind\": \"MessageEvent\", "
                + "\"source\": \"user\", "
                + "\"llm_message\": {\"role\":\"user\",\"content\":\"hi\"}}}"));
        ((int)res.StatusCode).ShouldBeLessThan(500);
        res.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Quota_Unknown_Org_Is_404_Not_500()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.PutAsJsonAsync(
            $"/api/v1/organizations/{Guid.NewGuid()}/quota",
            new { daily_max = 10, concurrent_max = 2 });
        res.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Keys_List_Does_Not_500()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.GetAsync("/api/v1/keys");
        ((int)res.StatusCode).ShouldBeLessThan(500);
    }

    [Fact]
    public async Task UsageOverride_Wrong_Expires_Type_Does_Not_500()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.PostAsync(
            $"/api/v1/users/{Guid.NewGuid()}/usage-override",
            Json("{\"expires_at\": 12345, \"limits\": {\"rpm\": 1}}"));
        ((int)res.StatusCode).ShouldBeLessThan(500);
    }
}