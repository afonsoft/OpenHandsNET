using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-11 API contract: skill CRUD + trigger resolution + hook CRUD +
/// conversation hooks view.
/// </summary>
public sealed class SkillHookEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-sk-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public SkillHookEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:DefaultImage", "bash:5");
            builder.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            builder.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"ohn-sk-ws-{Guid.NewGuid():N}"));
        });
        _http = TestAuth.Login(_factory);
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task Skills_Crud_RoundTrip()
    {
        var created = await _http.PostAsJsonAsync("/api/v1/skills", new
        {
            name = "api-skill",
            description = "from api",
            content_md = "# body",
            triggers = new[] { new { type = "keyword", value = "api" } },
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var doc = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = doc.GetProperty("id").GetGuid();

        var list = await _http.GetFromJsonAsync<JsonElement[]>("/api/v1/skills");
        list!.ShouldContain(s => s.GetProperty("id").GetGuid() == id);

        var patch = await _http.PatchAsJsonAsync($"/api/v1/skills/{id}",
            new { enabled = false });
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await _http.DeleteAsync($"/api/v1/skills/{id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _http.GetAsync($"/api/v1/skills/{id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Skill_Create_Rejects_Bad_Slug()
    {
        var resp = await _http.PostAsJsonAsync("/api/v1/skills", new
        {
            name = "INVALID NAME!!",
            content_md = "x",
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Builtin_Skills_Seeded_And_ReadOnly()
    {
        var list = await _http.GetFromJsonAsync<JsonElement[]>("/api/v1/skills");
        var builtin = list!.FirstOrDefault(s =>
            s.GetProperty("source").GetString() == "builtin");
        builtin.ValueKind.ShouldNotBe(JsonValueKind.Undefined);
        var id = builtin.GetProperty("id").GetGuid();

        (await _http.DeleteAsync($"/api/v1/skills/{id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Hooks_Crud_And_Conversation_View()
    {
        var created = await _http.PostAsJsonAsync("/api/v1/hooks", new
        {
            name = "audit",
            @event = "on_tool_post",
            action = "emit_event",
            payload = "tool ran",
            blocking = false,
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var doc = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = doc.GetProperty("id").GetGuid();

        var bad = await _http.PostAsJsonAsync("/api/v1/hooks", new
        {
            name = "x",
            @event = "bogus",
            action = "emit_event",
        });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var view = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/hooks");
        view.GetProperty("hooks").GetArrayLength().ShouldBeGreaterThan(0);

        (await _http.DeleteAsync($"/api/v1/hooks/{id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}