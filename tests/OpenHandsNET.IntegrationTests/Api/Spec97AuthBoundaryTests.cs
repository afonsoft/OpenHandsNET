using System.Net;
using System.Net.Http.Json;
using System.Text;

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

    // ---- RF-002: nightly wave — 500s restantes do fuzz -----------------

    [Fact]
    public async Task Chat_Completions_Empty_Body_Is_400_Not_500()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.PostAsync("/v1/chat/completions",
            new StringContent("", Encoding.UTF8, "application/json"));
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Knowledge_Suggestions_Invalid_Status_Is_400()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.GetAsync(
            "/api/v1/knowledge/suggestions?status=&scope=");
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        res = await http.GetAsync(
            "/api/v1/knowledge/suggestions?status=bogus");
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Knowledge_Suggestions_Valid_Status_Is_200()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.GetAsync(
            "/api/v1/knowledge/suggestions?status=pending");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        res = await http.GetAsync(
            "/api/v1/knowledge/suggestions?status=APPROVED");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        res = await http.GetAsync("/api/v1/knowledge/suggestions");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Org_Create_Empty_Name_Is_4xx_Not_500()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.PostAsJsonAsync("/api/v1/organizations",
            new { name = " ", slug = (string?)null });
        ((int)res.StatusCode).ShouldBeLessThan(500);
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Org_Create_Duplicate_Slug_Is_409_Not_500()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var slug = $"dup-{Guid.NewGuid():N}";
        (await http.PostAsJsonAsync("/api/v1/organizations",
            new { name = "Dup Org", slug })).EnsureSuccessStatusCode();
        var res = await http.PostAsJsonAsync("/api/v1/organizations",
            new { name = "Dup Org 2", slug });
        res.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Automation_Runs_From_To_Does_Not_500()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.GetAsync(
            "/api/v1/automations/runs?from=2024-01-01T00:00:00Z"
                + "&to=2030-01-01T00:00:00Z&take=10");
        ((int)res.StatusCode).ShouldBeLessThan(500);
    }

    [Fact]
    public async Task Checkpoints_Unknown_Conversation_Is_404_Not_503()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.GetAsync(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/checkpoints");
        res.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Byor_Disabled_Is_404_Not_503()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.GetAsync("/api/v1/keys/llm/byor");
        res.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task McpToolCall_Null_Body_Is_400_ProblemJson()
    {
        // JSON null em param de body inferido → short-circuit 400 vazio
        // (sem exceção) — middleware reescreve p/ problem+json (schema-
        // conforme) quando o endpoint declara ProducesProblem(400).
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.PostAsync("/api/v1/mcp/tool-call",
            new StringContent("null", Encoding.UTF8, "application/json"));
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        res.Content.Headers.ContentType?.MediaType
            .ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Scm_Manifest_Missing_RedirectUri_Is_400_Not_500()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.PostAsJsonAsync(
            "/api/v1/scm/github/manifest", new { app_name = "x" });
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Admin_Plans_Empty_Object_Is_400_Not_500()
    {
        // #218: {} deixa Name nulo → NRE no service. Validação devolve 400.
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.PostAsJsonAsync(
            "/api/v1/admin/plans", new { });
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Admin_Plans_Delete_Unknown_Is_404()
    {
        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.DeleteAsync(
            $"/api/v1/admin/plans/{Guid.NewGuid()}");
        res.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Scim_Users_Without_Token_Is_401()
    {
        var anon = _factory.CreateClient();
        var res = await anon.GetAsync("/scim/v2/Users");
        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        res = await anon.GetAsync($"/scim/v2/Users/{Guid.NewGuid()}");
        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_Users_Without_Token_Is_401_And_With_Token_Is_200()
    {
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/v1/admin/users"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var http = await TestAuth.LoginAsync(_factory);
        var res = await http.GetAsync("/api/v1/admin/users");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}