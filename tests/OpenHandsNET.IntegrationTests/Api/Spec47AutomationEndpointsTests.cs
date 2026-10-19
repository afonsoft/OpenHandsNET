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
/// SPEC-47 RF-003/RF-004: contrato de <c>/api/v1/automations</c> —
/// CRUD owner-scoped, run-now + idempotência, runs, hooks one-time
/// token, trigger anônimo (404 em token inválido, 429 rate-limit).
/// </summary>
public sealed class Spec47AutomationEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-spec47-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec47AutomationEndpointsTests()
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

    private static object ValidAutomation() => new
    {
        name = "nightly",
        prompt = "run the suite",
        schedule_kind = "interval",
        interval_minutes = 60,
    };

    [Fact]
    public async Task Crud_Roundtrip_OwnerScoped()
    {
        var alice = await LoginUserAsync("alice@example.com");
        var bob = await LoginUserAsync("bob@example.com");

        // create (validação: interval requer interval_minutes > 0)
        var bad = await alice.PostAsJsonAsync("/api/v1/automations", new
        {
            name = "x",
            prompt = "p",
            schedule_kind = "interval",
            interval_minutes = 0,
        });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var created = await alice.PostAsJsonAsync(
            "/api/v1/automations", ValidAutomation());
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var auto = await created.Content
            .ReadFromJsonAsync<JsonElement>();
        var id = auto.GetProperty("id").GetGuid();
        auto.GetProperty("name").GetString().ShouldBe("nightly");
        auto.GetProperty("next_run_at").ValueKind
            .ShouldBe(JsonValueKind.String);

        // get por id + list do owner
        (await alice.GetFromJsonAsync<JsonElement>(
            $"/api/v1/automations/{id}"))
            .GetProperty("name").GetString().ShouldBe("nightly");
        var list = await alice.GetFromJsonAsync<JsonElement>(
            "/api/v1/automations");
        list.GetArrayLength().ShouldBe(1);

        // bob não enxerga a automation da alice
        (await bob.GetAsync($"/api/v1/automations/{id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await bob.GetFromJsonAsync<JsonElement>("/api/v1/automations"))
            .GetArrayLength().ShouldBe(0);

        // patch enabled=false
        var patched = await alice.PatchAsJsonAsync(
            $"/api/v1/automations/{id}", new { enabled = false });
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await patched.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("enabled").GetBoolean().ShouldBeFalse();

        // delete
        (await alice.DeleteAsync($"/api/v1/automations/{id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await alice.DeleteAsync($"/api/v1/automations/{id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Anonymous_Requires_Auth()
    {
        var anon = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        (await anon.GetAsync("/api/v1/automations"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync(
            "/api/v1/automations", ValidAutomation()))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RunNow_Idempotent_And_RunsListed()
    {
        var http = await LoginUserAsync("runner@example.com");
        var id = (await (await http.PostAsJsonAsync(
                "/api/v1/automations", ValidAutomation()))
                .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var first = await http.PostAsync(
            $"/api/v1/automations/{id}/run-now", null);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var run = await first.Content.ReadFromJsonAsync<JsonElement>();
        run.GetProperty("conversation_id").ValueKind
            .ShouldBe(JsonValueKind.String);

        // segundo run-now enquanto running → 409 (idempotência)
        var dup = await http.PostAsync(
            $"/api/v1/automations/{id}/run-now", null);
        dup.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // histórico por automation + global — running + skipped
        // (tentativas negadas também entram no audit trail)
        var runs = await http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/automations/{id}/runs");
        runs.GetArrayLength().ShouldBe(2);
        runs.EnumerateArray()
            .Count(r => r.GetProperty("status").GetString() == "running")
            .ShouldBe(1);

        var all = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/automations/runs");
        all.GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Hook_OneTimeToken_AnonymousTrigger_And_RateLimit()
    {
        OpenHandsNET.Api.Endpoints.AutomationEndpoints
            .HookRateLimiter.Reset();

        var http = await LoginUserAsync("hooker@example.com");
        var id = (await (await http.PostAsJsonAsync(
                "/api/v1/automations", ValidAutomation()))
                .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var hook = await http.PostAsync(
            $"/api/v1/automations/{id}/hooks", null);
        hook.StatusCode.ShouldBe(HttpStatusCode.Created);
        var hookDto = await hook.Content.ReadFromJsonAsync<JsonElement>();
        var token = hookDto.GetProperty("token").GetString()!;
        token.ShouldStartWith("ohn_hook_");
        var hookId = hookDto.GetProperty("id").GetGuid();

        // listagem não vaza token — só hash no banco
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = db.AutomationHooks.Single(h => h.Id == hookId);
        row.TokenHash.ShouldNotBe(token);

        // token inválido → 404 (não vaza existência)
        var anon = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        var missing = await anon.PostAsync(
            "/api/v1/hooks/ohn_hook_nonexistent", null);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // trigger real → 202 + run
        var fired = await anon.PostAsync($"/api/v1/hooks/{token}", null);
        fired.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await fired.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("conversation_id").ValueKind
            .ShouldBe(JsonValueKind.String);

        // rate-limit por token: após o disparo acima restam 9 → a 10ª
        // tentativa dentro da janela é 429.
        for (var i = 0; i < 9; i++)
        {
            await anon.PostAsync($"/api/v1/hooks/{token}", null);
        }

        var limited = await anon.PostAsync($"/api/v1/hooks/{token}", null);
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // delete do hook desarma o trigger
        OpenHandsNET.Api.Endpoints.AutomationEndpoints
            .HookRateLimiter.Reset();
        (await http.DeleteAsync($"/api/v1/automations/hooks/{hookId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await anon.PostAsync($"/api/v1/hooks/{token}", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}