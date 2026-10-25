using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-97 (issue #218): FallbackPolicy — endpoints sem AllowAnonymous
/// exigem auth (fecha os 35 `ignored_auth`), e inputs malformados dão
/// 4xx em vez de 500 (os `not_a_server_error` do fuzz).
/// </summary>
public sealed class Spec97AuthBoundaryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-auth-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec97AuthBoundaryTests()
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

    private HttpClient Anon() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { HandleCookies = false });

    // ---- RF-001: default-deny real -------------------------------------

    [Theory]
    [InlineData("/api/v1/app-conversations")]
    [InlineData("/api/v1/app-conversations/count")]
    [InlineData("/api/v1/app-conversations/start-tasks")]
    [InlineData("/api/v1/conversation/00000000-0000-0000-0000-000000000000/events")]
    [InlineData("/api/v1/git/credentials")]
    [InlineData("/api/v1/hooks")]
    [InlineData("/api/v1/mcp/servers")]
    [InlineData("/api/v1/models")]
    [InlineData("/api/v1/pending-messages")]
    [InlineData("/api/v1/sandboxes")]
    [InlineData("/api/v1/settings")]
    [InlineData("/api/v1/skills")]
    [InlineData("/api/v1/secrets")]
    [InlineData("/api/v1/webhooks/secrets")]
    [InlineData("/api/v1/readiness")]
    public async Task Anonymous_Get_Is_401_Not_200_Nor_500(string path)
    {
        using var http = Anon();
        var res = await http.GetAsync(path);
        res.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized, $"{path} aceitou acesso anônimo");
    }

    [Theory]
    [InlineData("/api/v1/webhooks/conversations")]
    [InlineData("/api/v1/settings")]
    [InlineData("/api/v1/mcp/tool-call")]
    public async Task Anonymous_Write_Is_401(string path)
    {
        using var http = Anon();
        var res = await http.PostAsJsonAsync(path, new { });
        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---- RF-001: superfície pública intencional ------------------------

    [Theory]
    [InlineData("/api/v1/health")]
    [InlineData("/api/v1/ready")]
    [InlineData("/api/v1/config")]
    [InlineData("/api/v1/user")]
    [InlineData("/openapi/v1.json")]
    public async Task Intentional_Anonymous_Endpoints_Stay_Open(string path)
    {
        using var http = Anon();
        var res = await http.GetAsync(path);
        res.StatusCode.ShouldBe(
            HttpStatusCode.OK, $"{path} deveria ser público");
    }

    [Fact]
    public async Task Mcp_Oauth_State_Callback_Is_Anonymous()
    {
        using var http = Anon();
        (await http.PostAsJsonAsync("/api/v1/webhooks/mcp-oauth-state",
            new { }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    // ---- RF-002: 4xx em vez de 500 --------------------------------------

    [Fact]
    public async Task Secrets_Routes_Are_Not_Ambiguous()
    {
        // Antes: GET/PUT/DELETE /api/v1/secrets{,/{name}} mapeados 2x →
        // AmbiguousMatchException → 500. Agora um único dono.
        using var http = await TestAuth.LoginAsync(_factory);
        (await http.GetAsync("/api/v1/secrets"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await http.PutAsJsonAsync("/api/v1/secrets/MY_KEY",
            new { value = "v" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await http.GetAsync("/api/v1/secrets"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await http.DeleteAsync("/api/v1/secrets/MY_KEY"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Pending_Messages_Bad_Guid_Is_400_Not_500()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.PostAsJsonAsync("/api/v1/pending-messages",
            new { conversation_id = "not-a-guid" });
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Git_Credentials_Null_Body_Is_400_Not_500()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.PutAsJsonAsync(
            "/api/v1/git/credentials/example.com", (object?)null);
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Oidc_Login_Is_404_When_Provider_Is_Not_Oidc()
    {
        using var http = Anon();
        var res = await http.GetAsync("/oauth/oidc/login");
        res.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}