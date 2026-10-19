using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Infrastructure.Persistence;

using Shouldly;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-48 RF-003: contrato de
/// <c>/api/v1/organizations/{id}/security-policy</c> — GET default
/// balanced, PUT strict resolve campos, validação 400, scoping por org,
/// audit <c>security_policy_updated</c>.
/// </summary>
public sealed class Spec48SecurityPolicyEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-spec48-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec48SecurityPolicyEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Database:Provider", "Sqlite");
                b.UseSetting("Database:ConnectionString",
                    $"Data Source={_dbPath}");
                b.UseSetting("Observability:OtlpEndpoint", string.Empty);
                b.UseSetting("Sandbox:InstanceOwner",
                    $"test-{Guid.NewGuid():N}");
                b.UseSetting("Auth:Provider", "none");
                b.UseSetting("Auth:UserProvisioningEnabled", "true");
                b.UseSetting("MultiTenancy:Enabled", "true");
            });
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_dbPath)) { File.Delete(_dbPath); }
    }

    private HttpClient NewClient() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { HandleCookies = true });

    private async Task<(HttpClient http, string orgId)> LoginAsync(
        string email)
    {
        var http = NewClient();
        var res = await http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email, displayName = email.Split('@')[0] });
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var auth = await http.PostAsJsonAsync(
            "/api/v1/authenticate", new { });
        var doc = await auth.Content.ReadFromJsonAsync<JsonElement>();
        return (http,
            doc.GetProperty("user").GetProperty("current_org_id")
                .GetString()!);
    }

    private static string PolicyPath(string orgId) =>
        $"/api/v1/organizations/{orgId}/security-policy";

    [Fact]
    public async Task Get_Returns_Balanced_Default_When_Unset()
    {
        var (http, orgId) = await LoginAsync("sec-a@example.com");
        var res = await http.GetAsync(PolicyPath(orgId));
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("preset").GetString().ShouldBe("balanced");
        doc.GetProperty("ask_mcp_tools").GetBoolean().ShouldBeFalse();
        doc.GetProperty("min_confirm_risk").GetString().ShouldBe("high");
        doc.GetProperty("resume_strict").ValueKind
            .ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Put_Strict_Resolves_And_Audits()
    {
        var (http, orgId) = await LoginAsync("sec-b@example.com");
        var res = await http.PutAsJsonAsync(PolicyPath(orgId),
            new { preset = "strict" });
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("preset").GetString().ShouldBe("strict");
        doc.GetProperty("ask_mcp_tools").GetBoolean().ShouldBeTrue();
        doc.GetProperty("min_confirm_risk").GetString()
            .ShouldBe("medium");
        doc.GetProperty("resume_strict").GetBoolean().ShouldBeTrue();

        // GET roundtrip
        var get = await http.GetFromJsonAsync<JsonElement>(
            PolicyPath(orgId));
        get.GetProperty("preset").GetString().ShouldBe("strict");

        // audit
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AuditEvents.ShouldContain(e =>
            e.Action == "security_policy_updated");
    }

    [Fact]
    public async Task Put_Invalid_Values_Return_400()
    {
        var (http, orgId) = await LoginAsync("sec-c@example.com");
        (await http.PutAsJsonAsync(PolicyPath(orgId),
                new { preset = "bogus" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await http.PutAsJsonAsync(PolicyPath(orgId),
                new { preset = "strict", min_confirm_risk = "bogus" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_Null_Resets_To_Balanced()
    {
        var (http, orgId) = await LoginAsync("sec-d@example.com");
        await http.PutAsJsonAsync(PolicyPath(orgId),
            new { preset = "strict" });
        var res = await http.PutAsJsonAsync<object?>(
            PolicyPath(orgId), null);
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("preset").GetString().ShouldBe("balanced");
    }

    [Fact]
    public async Task Other_Org_Member_And_Anonymous_Denied()
    {
        var (_, orgA) = await LoginAsync("sec-e@example.com");
        var (other, _) = await LoginAsync("sec-f@example.com");

        (await other.GetAsync(PolicyPath(orgA))).StatusCode
            .ShouldBeOneOf(HttpStatusCode.Forbidden,
                HttpStatusCode.NotFound);
        (await other.PutAsJsonAsync(PolicyPath(orgA),
                new { preset = "strict" })).StatusCode
            .ShouldBeOneOf(HttpStatusCode.Forbidden,
                HttpStatusCode.NotFound);

        var anon = _factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                HandleCookies = false,
            });
        (await anon.GetAsync(PolicyPath(orgA))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }
}