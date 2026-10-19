using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-40 RF-005: subset ACP <c>/api/acp/*</c> — auth, validações e
/// 404s. Run real contra agente = e2e manual.
/// </summary>
public sealed class AcpEndpointsTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-acp-{Guid.NewGuid():N}.db");

    public AcpEndpointsTests()
    {
        var dbPath = _dbPath;
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Database:Provider", "Sqlite");
                builder.UseSetting(
                    "Database:ConnectionString", $"Data Source={dbPath}");
                builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
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

    private async Task<HttpClient> LoginAsync()
    {
        var http = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email = "acp@example.com", displayName = "acp" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        return http;
    }

    [Fact]
    public async Task Acp_Requires_Auth()
    {
        var anon = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        (await anon.PostAsJsonAsync("/api/acp/conversations",
            new { initial_prompt = "hi" }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anon.GetAsync($"/api/acp/conversations/{Guid.NewGuid()}"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anon.GetAsync(
            $"/api/acp/conversations/{Guid.NewGuid()}/events"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_Requires_Prompt()
    {
        var http = await LoginAsync();
        var res = await http.PostAsJsonAsync(
            "/api/acp/conversations", new { });
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_Prompt_Cancel_Events_404_Unknown()
    {
        var http = await LoginAsync();
        var id = Guid.NewGuid();
        (await http.GetAsync($"/api/acp/conversations/{id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await http.PostAsJsonAsync(
            $"/api/acp/conversations/{id}/prompt", new { prompt = "hi" }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await http.PostAsync($"/api/acp/conversations/{id}/cancel", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await http.GetAsync($"/api/acp/conversations/{id}/events"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Prompt_Requires_Prompt_Field()
    {
        var http = await LoginAsync();
        var res = await http.PostAsJsonAsync(
            $"/api/acp/conversations/{Guid.NewGuid()}/prompt", new { });
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}