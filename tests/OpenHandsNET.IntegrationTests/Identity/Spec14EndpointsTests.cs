using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenHandsNET.IntegrationTests.Identity;

/// <summary>
/// SPEC-14 endpoint flows: org usage aggregation, admin support-bundle
/// (contents + redaction) and sandbox_profile on conversation create.
/// </summary>
public sealed class Spec14EndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-s14-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec14EndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:DefaultImage", "bash:5");
            builder.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            builder.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"ohn-s14-ws-{Guid.NewGuid():N}"));
            builder.UseSetting("Auth:Provider", "none");
            builder.UseSetting("Auth:UserProvisioningEnabled", "true");
            builder.UseSetting("MultiTenancy:Enabled", "true");
            // planted secret — must never appear in the support bundle
            builder.UseSetting("AgentLlm:ApiKey", "sk-test-do-not-leak-123456");
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

    private HttpClient NewClient() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { HandleCookies = true });

    private async Task<(HttpClient http, JsonElement user)> LoginAsync(string email)
    {
        var http = NewClient();
        var res = await http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email, displayName = email.Split('@')[0] });
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var auth = await http.PostAsJsonAsync("/api/v1/authenticate", new { });
        var doc = await auth.Content.ReadFromJsonAsync<JsonElement>();
        return (http, doc.GetProperty("user"));
    }

    [Fact]
    public async Task Org_Usage_Endpoint_Returns_Aggregates()
    {
        var (http, user) = await LoginAsync("usage@example.com");
        var orgId = user.GetProperty("current_org_id").GetString();
        var res = await http.GetAsync($"/api/v1/organizations/{orgId}/usage");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("org_id").GetString().ShouldBe(orgId);
        doc.GetProperty("tokens_used").GetInt64().ShouldBe(0);
        doc.GetProperty("runs").GetInt32().ShouldBe(0);
        doc.TryGetProperty("sandbox_seconds", out _).ShouldBeTrue();
        doc.TryGetProperty("period", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Org_Usage_Requires_View_Billing()
    {
        var (adminHttp, admin) = await LoginAsync("admin@example.com");
        var (memberHttp, _) = await LoginAsync("member@example.com");
        var orgA = admin.GetProperty("current_org_id").GetString();
        // member's personal org differs — usage of the admin's org denied
        var res = await memberHttp.GetAsync(
            $"/api/v1/organizations/{orgA}/usage");
        res.StatusCode.ShouldBeOneOf(
            HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Support_Bundle_Is_Zip_With_Redacted_Content()
    {
        var (http, _) = await LoginAsync("support@example.com");
        var res = await http.GetAsync("/api/v1/support-bundle");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");

        await using var stream = await res.Content.ReadAsStreamAsync();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.Name).ToList();
        names.ShouldContain("readiness.json");
        names.ShouldContain("versions.json");
        names.ShouldContain("counts.json");
        names.ShouldContain("config.json");
        names.ShouldContain("logs-recent.txt");

        var config = await ReadEntryAsync(zip, "config.json");
        config.ShouldNotContain("sk-test-do-not-leak-123456");
        config.ShouldContain("***");
        var counts = await ReadEntryAsync(zip, "counts.json");
        counts.ShouldContain("conversations");
        var readiness = await ReadEntryAsync(zip, "readiness.json");
        readiness.ShouldContain("docker");
    }

    [Fact]
    public async Task Support_Bundle_Requires_Admin()
    {
        var (adminHttp, _) = await LoginAsync("sa@example.com");
        // fresh user in a different org, not super-admin
        var (userHttp, _) = await LoginAsync("pleb@example.com");
        // demote: remove pleb's super-admin? pleb isn't super anyway —
        // only the first user gets bootstrap super-admin.
        var res = await userHttp.GetAsync("/api/v1/support-bundle");
        res.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_Conversation_Accepts_Sandbox_Profile()
    {
        var (http, _) = await LoginAsync("profile@example.com");
        var res = await http.PostAsJsonAsync("/api/v1/app-conversations", new
        {
            initial_message = new
            {
                role = "user",
                content = new[] { new { type = "text", text = "hi" } },
                run = false,
            },
            sandbox_profile = "large",
        });
        res.StatusCode.ShouldBe(HttpStatusCode.Created);
        var task = await res.Content.ReadFromJsonAsync<JsonElement>();
        var convId = task.GetProperty("app_conversation_id").GetString();
        convId.ShouldNotBeNull();

        var conv = await http.GetAsync($"/api/v1/app-conversations/{convId}");
        conv.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = await conv.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("sandbox_profile").GetString().ShouldBe("large");
    }

    [Fact]
    public async Task Create_Conversation_Ignores_Invalid_Profile()
    {
        var (http, _) = await LoginAsync("badprofile@example.com");
        var res = await http.PostAsJsonAsync("/api/v1/app-conversations", new
        {
            initial_message = new
            {
                role = "user",
                content = new[] { new { type = "text", text = "hi" } },
                run = false,
            },
            sandbox_profile = "enormous",
        });
        res.StatusCode.ShouldBe(HttpStatusCode.Created);
        var task = await res.Content.ReadFromJsonAsync<JsonElement>();
        var convId = task.GetProperty("app_conversation_id").GetString()!;
        var doc = await (await http.GetAsync(
            $"/api/v1/app-conversations/{convId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("sandbox_profile").ValueKind
            .ShouldBe(JsonValueKind.Null);
    }

    private static async Task<string> ReadEntryAsync(ZipArchive zip, string name)
    {
        await using var s = zip.GetEntry(name)!.Open();
        using var r = new StreamReader(s);
        return await r.ReadToEndAsync();
    }
}