using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-40 RF-002: <c>GET /app-conversations/{id}/surfaces</c> — auth +
/// 404; o preenchimento real depende de agente vivo (e2e manual).
/// </summary>
public sealed class SurfacesEndpointsTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-surf-{Guid.NewGuid():N}.db");

    public SurfacesEndpointsTests()
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

    [Fact]
    public async Task Surfaces_Requires_Auth()
    {
        var anon = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        var res = await anon.GetAsync(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/surfaces");
        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Surfaces_404_For_Unknown_Conversation()
    {
        var http = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email = "s@example.com", displayName = "s" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var res = await http.GetAsync(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/surfaces");
        res.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}