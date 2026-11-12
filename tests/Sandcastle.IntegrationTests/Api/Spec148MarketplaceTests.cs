using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>
/// SPEC-148 API contract: catálogo offline, install por escopo com
/// gating de permissões (403/409/422), toggle/remove/upgrade e
/// materialização real visível nos endpoints de skills.
/// </summary>
public sealed class Spec148MarketplaceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-mkt-{Guid.NewGuid():N}.db");
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), $"sc-mkt-data-{Guid.NewGuid():N}");
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), $"sc-mkt-home-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Func<string?> _prevHome;
    private readonly Func<string> _prevVersion;

    public Spec148MarketplaceTests()
    {
        // Seams do serviço — agentes materializam num HOME temporário e
        // a versão do app satisfaz min_app_version dos seeds.
        _prevHome = Infrastructure.Marketplace.MarketplaceService.HomeResolver;
        _prevVersion = Infrastructure.Marketplace.MarketplaceService
            .AppVersionResolver;
        Infrastructure.Marketplace.MarketplaceService.HomeResolver =
            () => _home;
        Infrastructure.Marketplace.MarketplaceService.AppVersionResolver =
            () => "1.0.0";

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath};Default Timeout=10");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:DefaultImage", "bash:5");
            builder.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            builder.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"sc-mkt-ws-{Guid.NewGuid():N}"));
            builder.UseSetting("Marketplace:DataDir", _dataDir);
        });
    }

    public void Dispose()
    {
        Infrastructure.Marketplace.MarketplaceService.HomeResolver = _prevHome;
        Infrastructure.Marketplace.MarketplaceService.AppVersionResolver =
            _prevVersion;
        _factory.Dispose();
        foreach (var p in new[] { _dbPath, _dataDir, _home })
        {
            if (File.Exists(p))
            {
                File.Delete(p);
            }
            else if (Directory.Exists(p))
            {
                Directory.Delete(p, true);
            }
        }
    }

    // ------------------------------------------------------------- catalog

    [Fact]
    public async Task Entries_Offline_Bundled_Catalog()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var res = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/marketplace/entries");
        var items = res.GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBeGreaterThanOrEqualTo(10);
        items.ShouldContain(e => e.GetProperty("id").GetString() == "iterate");
        var kinds = items.Select(e => e.GetProperty("kind").GetString())
            .ToHashSet();
        kinds.ShouldBeSubsetOf(
            ["skill", "mcp-server", "agent", "hook", "bundle"]);
        kinds.ShouldContain("mcp-server");
        kinds.ShouldContain("bundle");
    }

    [Fact]
    public async Task Entry_Detail_And_404()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var detail = await http.GetAsync(
            "/api/v1/marketplace/entries/iterate");
        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = await detail.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("kind").GetString().ShouldBe("skill");
        doc.GetProperty("files").GetArrayLength()
            .ShouldBeGreaterThan(0);

        (await http.GetAsync("/api/v1/marketplace/entries/does-not-exist"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------- install

    [Fact]
    public async Task Install_SystemScope_Materializes_Globally()
    {
        // Primeiro usuário = superadmin → instala em system. Skill
        // materializada sem owner → global, visível no list de /skills
        // (visão global single-tenant) — espelho do que os agentes
        // resolvem por conversa.
        var http = await TestAuth.LoginAsync(_factory);
        var created = await http.PostAsJsonAsync(
            "/api/v1/marketplace/install",
            new { entry_id = "iterate", scope = "system" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var row = await created.Content.ReadFromJsonAsync<JsonElement>();
        var installedId = row.GetProperty("id").GetGuid();

        var skills = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/skills");
        var skill = skills!.EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "iterate");
        skill.GetProperty("source").GetString().ShouldBe("marketplace");
        skill.GetProperty("enabled").GetBoolean().ShouldBeTrue();

        // Toggle off → materialized row disabled.
        var patch = await http.PatchAsJsonAsync(
            $"/api/v1/marketplace/installed/{installedId}",
            new { enabled = false });
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);
        skills = await http.GetFromJsonAsync<JsonElement>("/api/v1/skills");
        skills!.EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "iterate")
            .GetProperty("enabled").GetBoolean().ShouldBeFalse();

        // Remove → desmaterializa.
        (await http.DeleteAsync(
            $"/api/v1/marketplace/installed/{installedId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        skills = await http.GetFromJsonAsync<JsonElement>("/api/v1/skills");
        skills!.EnumerateArray().ShouldNotContain(
            s => s.GetProperty("name").GetString() == "iterate");
    }

    [Fact]
    public async Task Install_UserScope_Recorded_And_Listed()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var created = await http.PostAsJsonAsync(
            "/api/v1/marketplace/install",
            new { entry_id = "iterate", scope = "user" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var row = await created.Content.ReadFromJsonAsync<JsonElement>();
        row.GetProperty("scope").GetString().ShouldBe("user");
        row.GetProperty("scope_id").GetString().ShouldNotBeNullOrEmpty();

        // user-scope fica fora da list global de /skills (isolamento) —
        // a superfície correta é /installed.
        var skills = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/skills");
        skills!.EnumerateArray().ShouldNotContain(
            s => s.GetProperty("name").GetString() == "iterate");

        var list = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/marketplace/installed?scope=user");
        list.GetProperty("items").EnumerateArray().ShouldContain(
            i => i.GetProperty("entry_id").GetString() == "iterate");

        // Cleanup para não vazar entre asserts seguintes.
        (await http.DeleteAsync(
            $"/api/v1/marketplace/installed/{row.GetProperty("id").GetGuid()}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Install_Duplicate_409_And_Listed()
    {
        var http = await TestAuth.LoginAsync(_factory);
        (await http.PostAsJsonAsync("/api/v1/marketplace/install",
            new { entry_id = "write-specs", scope = "user" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await http.PostAsJsonAsync("/api/v1/marketplace/install",
            new { entry_id = "write-specs", scope = "user" }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var list = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/marketplace/installed?scope=user");
        var items = list.GetProperty("items").EnumerateArray().ToList();
        items.ShouldContain(i =>
            i.GetProperty("entry_id").GetString() == "write-specs");
    }

    [Fact]
    public async Task Install_PermissionsRequired_422()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var denied = await http.PostAsJsonAsync(
            "/api/v1/marketplace/install",
            new { entry_id = "k8s-ops", scope = "user" });
        denied.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var ok = await http.PostAsJsonAsync("/api/v1/marketplace/install",
            new
            {
                entry_id = "k8s-ops",
                scope = "user",
                accept_permissions = true,
            });
        ok.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Upgrade_AlreadyLatest_409()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var created = await http.PostAsJsonAsync(
            "/api/v1/marketplace/install",
            new { entry_id = "git-workflow", scope = "user" });
        var row = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = row.GetProperty("id").GetGuid();

        (await http.PostAsync(
            $"/api/v1/marketplace/upgrade/{id}", null))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ------------------------------------------------------------- gating

    [Fact]
    public async Task User_Without_Permission_403()
    {
        // Primeiro usuário = superadmin com org default. Segundo usuário
        // sem org não tem marketplace.install → 403 do policy provider.
        _ = await TestAuth.LoginAsync(_factory, "owner@local");
        var member = await TestAuth.LoginAsync(_factory, "solo@local");
        (await member.GetAsync("/api/v1/marketplace/entries"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task OrgScope_Member_403_Owner_201()
    {
        var owner = await TestAuth.LoginAsync(_factory, "own@local");
        var orgs = await owner.GetFromJsonAsync<JsonElement>(
            "/api/v1/organizations");
        var orgId = orgs![0].GetProperty("id").GetGuid();

        var invite = await owner.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/members/invite",
            new { emails = new[] { "mem@local" }, role = "member" });
        invite.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await invite.Content.ReadFromJsonAsync<JsonElement>())[0]
            .GetProperty("token").GetString()!;

        var member = await TestAuth.LoginAsync(_factory, "mem@local");
        (await member.PostAsJsonAsync(
            "/api/v1/organizations/members/invite/accept",
            new { token })).StatusCode.ShouldBe(HttpStatusCode.OK);

        // member tem marketplace.install (user scope) mas não manage.
        (await member.PostAsJsonAsync("/api/v1/marketplace/install",
            new { entry_id = "iterate", scope = "user" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await member.PostAsJsonAsync("/api/v1/marketplace/install",
            new
            {
                entry_id = "iterate",
                scope = "org",
                scope_id = orgId.ToString(),
            })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // owner (org admin) instala na org.
        (await owner.PostAsJsonAsync("/api/v1/marketplace/install",
            new
            {
                entry_id = "iterate",
                scope = "org",
                scope_id = orgId.ToString(),
            })).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task SystemScope_NonSuperAdmin_403()
    {
        _ = await TestAuth.LoginAsync(_factory, "first@local");
        var member = await TestAuth.LoginAsync(_factory, "second@local");
        // segundo usuário nem marketplace.install tem → 403 do grupo.
        (await member.PostAsJsonAsync("/api/v1/marketplace/install",
            new { entry_id = "iterate", scope = "system" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CrossUser_Row_Management_403()
    {
        var a = await TestAuth.LoginAsync(_factory, "a@local");
        var created = await a.PostAsJsonAsync(
            "/api/v1/marketplace/install",
            new { entry_id = "sql-code-review", scope = "user" });
        var row = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = row.GetProperty("id").GetGuid();

        // B entra como member da org de A → tem marketplace.install e
        // passa o policy do grupo; o gate por linha (user-scope de A)
        // é o que nega com 403.
        var orgs = await a.GetFromJsonAsync<JsonElement>(
            "/api/v1/organizations");
        var orgId = orgs![0].GetProperty("id").GetGuid();
        var invite = await a.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/members/invite",
            new { emails = new[] { "b@local" }, role = "member" });
        var token = (await invite.Content.ReadFromJsonAsync<JsonElement>())[0]
            .GetProperty("token").GetString()!;
        var b = await TestAuth.LoginAsync(_factory, "b@local");
        (await b.PostAsJsonAsync(
            "/api/v1/organizations/members/invite/accept",
            new { token })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await b.PatchAsJsonAsync(
            $"/api/v1/marketplace/installed/{id}", new { enabled = false }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await b.DeleteAsync($"/api/v1/marketplace/installed/{id}"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await b.PostAsync($"/api/v1/marketplace/upgrade/{id}", null))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}