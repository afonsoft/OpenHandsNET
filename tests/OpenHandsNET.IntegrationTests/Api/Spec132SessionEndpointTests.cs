using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

using Xunit;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-132: <c>GET /api/v1/auth/session</c> — probe anônimo que o
/// client WASM usa para decidir entre boot direto (none) e a tela
/// /auth (providers que exigem sessão).
/// </summary>
public sealed class Spec132SessionEndpointTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-spec132-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec132SessionEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Database:Provider", "Sqlite");
                b.UseSetting("Database:ConnectionString",
                    $"Data Source={_dbPath};Default Timeout=10");
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

    [Fact]
    public async Task Session_Anonymous_Reports_Provider_None()
    {
        using var http = _factory.CreateClient();
        var doc = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/auth/session");
        doc.GetProperty("provider").GetString().ShouldBe("none");
        doc.GetProperty("authenticated").GetBoolean().ShouldBeFalse();
        doc.GetProperty("user").ValueKind
            .ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Session_DevLogin_Reports_User()
    {
        using var http = await TestAuth.LoginAsync(
            _factory, "spec132@local");
        var doc = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/auth/session");
        doc.GetProperty("authenticated").GetBoolean().ShouldBeTrue();
        doc.GetProperty("user").GetProperty("email").GetString()
            .ShouldBe("spec132@local");
    }

    [Fact]
    public async Task Session_Logout_Returns_To_Anonymous()
    {
        using var http = await TestAuth.LoginAsync(
            _factory, "spec132b@local");
        (await http.PostAsync("/api/v1/auth/logout", null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/auth/session");
        doc.GetProperty("authenticated").GetBoolean().ShouldBeFalse();
    }
}