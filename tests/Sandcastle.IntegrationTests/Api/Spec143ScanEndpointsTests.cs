using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Application.Scans;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Scans;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>
/// SPEC-143: contrato dos endpoints de code scan —
/// <c>POST /api/v1/scans</c> (202 + ids), lista com cursor, detalhe,
/// findings feed (404 no scan órfão), PATCH triagem, fix 404/409 e
/// fix-bulk. Seeds de findings via AppDbContext scoped.
/// </summary>
public sealed class Spec143ScanEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-spec143-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec143ScanEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Database:Provider", "Sqlite");
                b.UseSetting("Database:ConnectionString",
                    $"Data Source={_dbPath};Default Timeout=10");
                b.UseSetting("Observability:OtlpEndpoint", string.Empty);
                b.UseSetting("Sandbox:InstanceOwner",
                    $"test-{Guid.NewGuid():N}");
                b.UseSetting("Auth:Provider", "none");
                b.UseSetting("Auth:UserProvisioningEnabled", "true");
                // ScanWorker nunca dispara nos testes de contrato.
                b.UseSetting("Scan:PollIntervalSeconds", "3600");
                b.UseSetting("Scan:ChildTimeoutMinutes", "120");
            });
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_dbPath)) { File.Delete(_dbPath); }
    }

    private HttpClient NewClient() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { HandleCookies = true });

    private async Task<HttpClient> LoginUserAsync(string email)
    {
        var http = NewClient();
        var res = await http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email, displayName = email.Split('@')[0] });
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        return http;
    }

    private static object ScanBody(IReadOnlyList<string>? repos = null,
        string goal = "find security issues",
        string? profile = "security", string? effort = "low") => new
        {
            repositories = repos ?? new[] { "org/repo" },
            goal,
            profile,
            effort,
        };

    private async Task<Guid> SeedFindingAsync(
        Guid scanId, string repo = "org/repo",
        string path = "src/A.cs", string severity = "high",
        string status = "open", string remediation = "unassigned")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<AppDbContext>();
        var orgId = db.Scans.IgnoreQueryFilters()
            .Where(s => s.Id == scanId).Select(s => s.OrgId)
            .FirstOrDefault();
        var f = new ScanFindingRecord
        {
            Id = Guid.NewGuid(),
            ScanId = scanId,
            OrgId = orgId,
            Repository = repo,
            Path = path,
            StartLine = 10,
            Kind = "security",
            Severity = severity,
            Title = "issue",
            Description = "desc",
            Status = status,
            RemediationState = remediation,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.ScanFindings.Add(f);
        await db.SaveChangesAsync();
        return f.Id;
    }

    [Fact]
    public async Task Create_Returns_202_With_Ids()
    {
        var http = await LoginUserAsync("scan1@example.com");

        var res = await http.PostAsJsonAsync(
            "/api/v1/scans", ScanBody());

        res.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("scan_id").GetGuid().ShouldNotBe(Guid.Empty);
        json.GetProperty("coordinator_conversation_id").GetGuid()
            .ShouldNotBe(Guid.Empty);
        // alias de compat com a tabela da spec.
        json.GetProperty("conversation_id").GetGuid()
            .ShouldBe(json.GetProperty("coordinator_conversation_id")
                .GetGuid());
        json.GetProperty("status").GetString().ShouldBe("queued");
    }

    [Fact]
    public async Task Create_Validation_Errors_Are_400()
    {
        var http = await LoginUserAsync("scan2@example.com");

        (await http.PostAsJsonAsync("/api/v1/scans",
            ScanBody(profile: "bogus"))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await http.PostAsJsonAsync("/api/v1/scans",
            ScanBody(effort: "extreme"))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await http.PostAsJsonAsync("/api/v1/scans",
            ScanBody(repos: []))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await http.PostAsJsonAsync("/api/v1/scans",
            ScanBody(goal: "  "))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_Detail_And_Cursor_Pagination()
    {
        var http = await LoginUserAsync("scan3@example.com");

        var r1 = await http.PostAsJsonAsync(
            "/api/v1/scans", ScanBody(goal: "first"));
        var id1 = (await r1.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("scan_id").GetGuid();
        var r2 = await http.PostAsJsonAsync(
            "/api/v1/scans", ScanBody(goal: "second"));
        var id2 = (await r2.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("scan_id").GetGuid();

        // detalhe
        var det = await http.GetAsync($"/api/v1/scans/{id1}");
        det.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detJson = await det.Content.ReadFromJsonAsync<JsonElement>();
        detJson.GetProperty("goal").GetString().ShouldBe("first");
        detJson.GetProperty("repositories")[0].GetString()
            .ShouldBe("org/repo");

        // lista com cursor limit=1 → 2 páginas.
        var p1 = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/scans?limit=1");
        var items1 = p1.GetProperty("items");
        items1.GetArrayLength().ShouldBe(1);
        var next = p1.GetProperty("next_page_id").GetString();
        next.ShouldNotBeNull();
        var p2 = await http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/scans?limit=1&cursor={next}");
        p2.GetProperty("items").GetArrayLength().ShouldBe(1);
        var seen = new[]
        {
            items1[0].GetProperty("id").GetGuid(),
            p2.GetProperty("items")[0].GetProperty("id").GetGuid(),
        };
        seen.ShouldBeSubsetOf([id1, id2]);
        seen.Distinct().Count().ShouldBe(2);

        // 404 em scan inexistente.
        (await http.GetAsync($"/api/v1/scans/{Guid.NewGuid()}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Findings_Feed_Filters_Paginate_Triage_Fix()
    {
        var http = await LoginUserAsync("scan4@example.com");
        var res = await http.PostAsJsonAsync(
            "/api/v1/scans", ScanBody());
        var scanId = (await res.Content
            .ReadFromJsonAsync<JsonElement>())
            .GetProperty("scan_id").GetGuid();
        var f1 = await SeedFindingAsync(scanId, severity: "critical");
        await SeedFindingAsync(scanId, path: "src/B.cs",
            severity: "low");

        // feed do scan — filtros por severidade + cursor first=1.
        var page = await http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/scans/{scanId}/findings?severity=critical");
        page.GetProperty("items").GetArrayLength().ShouldBe(1);
        page.GetProperty("items")[0].GetProperty("id").GetGuid()
            .ShouldBe(f1);

        var p1 = await http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/scans/{scanId}/findings?first=1");
        var next = p1.GetProperty("next_page_id").GetString();
        next.ShouldNotBeNull();
        var p2 = await http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/scans/{scanId}/findings?first=1&after={next}");
        p2.GetProperty("items").GetArrayLength().ShouldBe(1);

        // scan inexistente → 404 (não vaza findings órfãos).
        (await http.GetAsync(
            $"/api/v1/scans/{Guid.NewGuid()}/findings")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);

        // PATCH triagem — open → reviewed → dismissed → reopen.
        var patch = await http.PatchAsJsonAsync(
            $"/api/v1/scan-findings/{f1}", new { status = "reviewed" });
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await patch.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString().ShouldBe("reviewed");
        (await http.PatchAsJsonAsync(
            $"/api/v1/scan-findings/{f1}",
            new { status = "bogus" })).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await http.PatchAsJsonAsync(
            $"/api/v1/scan-findings/{Guid.NewGuid()}",
            new { status = "open" })).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);

        // FIX — explícito, 202 + conversation linkada; segunda vez = 409.
        var fix = await http.PostAsync(
            $"/api/v1/scan-findings/{f1}/fix", null);
        fix.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var fixJson = await fix.Content
            .ReadFromJsonAsync<JsonElement>();
        fixJson.GetProperty("conversation_id").GetGuid()
            .ShouldNotBe(Guid.Empty);
        (await http.PostAsync(
            $"/api/v1/scan-findings/{f1}/fix", null)).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);

        // fix-bulk — um já-assigned (409-item) + um inexistente.
        var f2 = await SeedFindingAsync(scanId, path: "src/C.cs");
        var bulk = await http.PostAsJsonAsync(
            "/api/v1/scan-findings/fix-bulk",
            new { ids = new[] { f1, f2, Guid.NewGuid() } });
        bulk.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var items = (await bulk.Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        items.GetArrayLength().ShouldBe(3);
        items.EnumerateArray().Count(i =>
            i.GetProperty("error").ValueKind
                == JsonValueKind.String).ShouldBe(2);
    }

    [Fact]
    public async Task Unauthenticated_Is_401()
    {
        var http = NewClient();
        (await http.PostAsJsonAsync("/api/v1/scans", ScanBody()))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await http.GetAsync("/api/v1/scans"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}