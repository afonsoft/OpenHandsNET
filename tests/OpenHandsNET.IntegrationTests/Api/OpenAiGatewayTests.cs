using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-40 RF-001: contrato do gateway <c>/v1/chat/completions</c> —
/// auth, erros em formato OpenAI e rejeições documentadas.
/// (Run real contra agente = e2e manual; aqui só o contrato HTTP.)
/// </summary>
public sealed class OpenAiGatewayTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-gw-{Guid.NewGuid():N}.db");

    public OpenAiGatewayTests()
    {
        var dbPath = _dbPath;
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Database:Provider", "Sqlite");
                builder.UseSetting(
                    "Database:ConnectionString", $"Data Source={dbPath}");
                builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
                builder.UseSetting("Sandbox:DefaultImage", "bash:5");
                builder.UseSetting("Auth:Provider", "none");
                builder.UseSetting("Auth:UserProvisioningEnabled", "true");
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

    private async Task<HttpClient> LoginAsync(string email)
    {
        var http = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true });
        var res = await http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email, displayName = "gw" });
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        return http;
    }

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error");

    [Fact]
    public async Task ChatCompletions_Requires_Auth()
    {
        var anon = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        var res = await anon.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "x",
            messages = new[] { new { role = "user", content = "hi" } },
        });
        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChatCompletions_Rejects_Tools_And_ResponseFormat()
    {
        var http = await LoginAsync("gw1@example.com");
        var res = await http.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "x",
            messages = new[] { new { role = "user", content = "hi" } },
            tools = new[] { new { type = "function" } },
        });
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var err = await ErrorAsync(res);
        err.GetProperty("type").GetString()!
            .ShouldBe("invalid_request_error");
        err.GetProperty("message").GetString()!.ShouldContain("tools");
    }

    [Fact]
    public async Task ChatCompletions_Rejects_N_Greater_Than_1()
    {
        var http = await LoginAsync("gw2@example.com");
        var res = await http.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "x", n = 2,
            messages = new[] { new { role = "user", content = "hi" } },
        });
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorAsync(res)).GetProperty("message").GetString()!
            .ShouldContain("'n' > 1");
    }

    [Fact]
    public async Task ChatCompletions_Requires_Messages()
    {
        var http = await LoginAsync("gw3@example.com");
        var res = await http.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "x", messages = Array.Empty<object>(),
        });
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorAsync(res)).GetProperty("message").GetString()!
            .ShouldContain("messages");
    }

    [Fact]
    public async Task ChatCompletions_Attach_Missing_Conversation_404()
    {
        var http = await LoginAsync("gw4@example.com");
        var req = new HttpRequestMessage(HttpMethod.Post,
            "/v1/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = "x",
                messages = new[] { new { role = "user", content = "hi" } },
            }),
        };
        req.Headers.Add("X-Conversation-Id", Guid.NewGuid().ToString());
        var res = await http.SendAsync(req);
        res.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var err = await ErrorAsync(res);
        err.GetProperty("type").GetString()!
            .ShouldBe("invalid_request_error");
    }
}
