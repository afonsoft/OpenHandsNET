using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Infrastructure.Persistence;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>
/// SPEC-151 API contract: <c>/api/v1/alerts/sources</c> CRUD,
/// <c>/api/v1/alerts/events</c> list/patch/create-session e o webhook
/// inbound <c>/api/v1/webhooks/alerts/{id}</c> — signature fail-closed
/// (401 sem/ruim), dedup de entrega, evento persistido + unrouted.
/// </summary>
public sealed class Spec151AlertEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-151-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec151AlertEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Database:Provider", "Sqlite");
                builder.UseSetting("Database:ConnectionString",
                    $"Data Source={_dbPath};Default Timeout=10");
                builder.UseSetting("Observability:OtlpEndpoint",
                    string.Empty);
                builder.UseSetting("Sandbox:DefaultImage", "bash:5");
                builder.UseSetting("Sandbox:InstanceOwner",
                    $"test-{Guid.NewGuid():N}");
                builder.UseSetting("Sandbox:WorkspaceRoot",
                    Path.Combine(Path.GetTempPath(),
                        $"sc-151-ws-{Guid.NewGuid():N}"));
                builder.UseEnvironment("Development");
            });
        _http = _factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                HandleCookies = true,
            });
        using var login = _http.PostAsJsonAsync(
            "/api/v1/auth/dev-login",
            new { email = "spec151@local", displayName = "spec151" })
            .GetAwaiter().GetResult();
        login.EnsureSuccessStatusCode();
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

    private static string Hmac(string secret, byte[] payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), payload));

    private static ByteArrayContent Body(string json) =>
        new(Encoding.UTF8.GetBytes(json))
        {
            Headers = { { "Content-Type", "application/json" } },
        };

    private async Task<Guid> CreateSourceAsync(
        string provider = "sentry", string secret = "sig-secret")
    {
        var resp = await _http.PostAsJsonAsync(
            "/api/v1/alerts/sources", new
            {
                provider,
                name = "src-1",
                secret_value = secret,
                repo_mapping = new Dictionary<string, string>
                {
                    ["web-api"] = "org/web-api",
                },
                triage_severity_min = "high",
            });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK,
            await resp.Content.ReadAsStringAsync());
        var doc = await resp.Content
            .ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Sources_Crud_And_Validation()
    {
        var id = await CreateSourceAsync();

        var list = await _http.GetFromJsonAsync<JsonElement>(
            "/api/v1/alerts/sources");
        list.GetProperty("items").GetArrayLength().ShouldBe(1);
        var src = list.GetProperty("items")[0];
        src.GetProperty("provider").GetString().ShouldBe("sentry");
        src.GetProperty("webhook_url").GetString()
            .ShouldBe($"/api/v1/webhooks/alerts/{id}");
        // secret_value nunca volta — só o ref gerado
        src.GetProperty("secret_ref").GetString()
            .ShouldNotBeNullOrEmpty();

        var patch = await _http.PatchAsJsonAsync(
            $"/api/v1/alerts/sources/{id}", new
            {
                auto_create_session = true,
                triage_severity_min = "critical",
            });
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);

        var del = await _http.DeleteAsync(
            $"/api/v1/alerts/sources/{id}");
        del.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var bad = await _http.PostAsJsonAsync(
            "/api/v1/alerts/sources", new
            {
                provider = "datadog",
                name = "x",
                secret_value = "y",
            });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Webhook_NoSignature_401_NothingPersisted()
    {
        var id = await CreateSourceAsync();
        var resp = await _http.PostAsync(
            $"/api/v1/webhooks/alerts/{id}",
            Body("""{"action":"created","data":{"issue":{"id":"i1","title":"t","status":"unresolved","project":{"slug":"web-api"}}}}"""));
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var events = await _http.GetFromJsonAsync<JsonElement>(
            "/api/v1/alerts/events");
        events.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Webhook_BadSignature_401()
    {
        var id = await CreateSourceAsync();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/webhooks/alerts/{id}");
        req.Content = Body("{}");
        req.Headers.Add("sentry-hook-signature", "deadbeef");
        var resp = await _http.SendAsync(req);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task IngestToEvent_ValidSignature_PersistsAndRoutes()
    {
        var id = await CreateSourceAsync();
        var payload =
            """{"action":"created","data":{"issue":{"id":"I-77","title":"boom","level":"error","status":"unresolved","culprit":"pay.checkout","project":{"slug":"web-api"},"permalink":"https://sentry.io/issues/I-77"}}}""";
        var bytes = Encoding.UTF8.GetBytes(payload);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/webhooks/alerts/{id}");
        req.Content = new ByteArrayContent(bytes);
        req.Content.Headers.Add("Content-Type", "application/json");
        req.Headers.Add("sentry-hook-signature",
            Hmac("sig-secret", bytes));
        req.Headers.Add("Sentry-Hook-UUID", "del-77");
        var resp = await _http.SendAsync(req);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var events = await _http.GetFromJsonAsync<JsonElement>(
            "/api/v1/alerts/events?severity=high");
        var items = events.GetProperty("items");
        items.GetArrayLength().ShouldBe(1);
        var evt = items[0];
        evt.GetProperty("external_id").GetString().ShouldBe("I-77");
        evt.GetProperty("repository").GetString()
            .ShouldBe("org/web-api");
        evt.GetProperty("unrouted").GetBoolean().ShouldBeFalse();
        var evtId = evt.GetProperty("id").GetGuid();

        // reenvio com a mesma entrega → duplicate (200, sem duplicar)
        using var dup = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/webhooks/alerts/{id}");
        dup.Content = new ByteArrayContent(bytes);
        dup.Content.Headers.Add("Content-Type", "application/json");
        dup.Headers.Add("sentry-hook-signature",
            Hmac("sig-secret", bytes));
        dup.Headers.Add("Sentry-Hook-UUID", "del-77");
        (await _http.SendAsync(dup)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        (await _http.GetFromJsonAsync<JsonElement>(
            "/api/v1/alerts/events")).GetProperty("items")
            .GetArrayLength().ShouldBe(1);

        // dismiss → patch
        var patch = await _http.PatchAsJsonAsync(
            $"/api/v1/alerts/events/{evtId}", new { status = "dismissed" });
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UnroutedEvent_CreateSession_409()
    {
        var id = await CreateSourceAsync();
        // payload sem project/service — sem chave no mapping → unrouted
        var payload =
            """{"action":"created","data":{"issue":{"id":"I-99","title":"orphan","level":"fatal","status":"unresolved"}}}""";
        var bytes = Encoding.UTF8.GetBytes(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/webhooks/alerts/{id}");
        req.Content = new ByteArrayContent(bytes);
        req.Content.Headers.Add("Content-Type", "application/json");
        req.Headers.Add("sentry-hook-signature",
            Hmac("sig-secret", bytes));
        (await _http.SendAsync(req)).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        var events = await _http.GetFromJsonAsync<JsonElement>(
            "/api/v1/alerts/events?unrouted=true");
        var evt = events.GetProperty("items")[0];
        evt.GetProperty("unrouted").GetBoolean().ShouldBeTrue();
        var evtId = evt.GetProperty("id").GetGuid();

        var resp = await _http.PostAsync(
            $"/api/v1/alerts/events/{evtId}/create-session", null);
        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // route manual → repository set → create-session sai do 409
        var route = await _http.PatchAsJsonAsync(
            $"/api/v1/alerts/events/{evtId}",
            new { repository = "org/web-api" });
        route.StatusCode.ShouldBe(HttpStatusCode.OK);
        var retry = await _http.PostAsync(
            $"/api/v1/alerts/events/{evtId}/create-session", null);
        retry.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Events_RequireAuth()
    {
        using var anon = _factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                HandleCookies = false,
            });
        (await anon.GetAsync("/api/v1/alerts/events")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await anon.GetAsync("/api/v1/alerts/sources")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }
}