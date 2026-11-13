using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Eventing;
using Sandcastle.Infrastructure.Persistence;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>
/// SPEC-149 API contract: <c>GET /api/v1/llm/routes</c>,
/// <c>GET|PUT /api/v1/organizations/{id}/routing-rules</c> (admin+;
/// member ⇒ 403 — allowlist internals não vazam) e
/// <c>GET /api/v1/usage/llm?by=role</c>.
/// </summary>
public sealed class Spec149ModelRouterApiTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-149-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;
    private readonly Guid _userId;

    public Spec149ModelRouterApiTests()
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
                        $"sc-149-ws-{Guid.NewGuid():N}"));
                builder.UseEnvironment("Development");
            });
        var options = new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
        };
        _http = _factory.CreateClient(options);
        using var login = _http.PostAsJsonAsync(
            "/api/v1/auth/dev-login",
            new { email = "spec149@local", displayName = "spec149" })
            .GetAwaiter().GetResult();
        login.EnsureSuccessStatusCode();
        _userId = login.Content
            .ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult()
            .GetProperty("user_id").GetGuid();
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

    private AppDbContext Db()
    {
        var scope = _factory.Services.GetRequiredService<
            IServiceScopeFactory>().CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    private Guid DefaultOrgId()
    {
        using var db = Db();
        return Guid.Parse(db.Users.Find(_userId)!.CurrentOrgId!);
    }

    // ---------- GET /api/v1/llm/routes ----------

    [Fact]
    public async Task Routes_ReturnsCatalog()
    {
        var resp = await _http.GetAsync("/api/v1/llm/routes");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK,
            await resp.Content.ReadAsStringAsync());
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        doc.ValueKind.ShouldBe(JsonValueKind.Array);
    }

    // ---------- routing-rules ----------

    [Fact]
    public async Task Rules_Get_Empty_Document_When_Unset()
    {
        var org = DefaultOrgId();
        var doc = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/organizations/{org}/routing-rules");

        doc.GetProperty("version").GetString()
            .ShouldBe("routing_rules_v1");
        doc.GetProperty("rules").ValueKind
            .ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task Rules_Put_Invalid_Schema_Returns400()
    {
        var org = DefaultOrgId();
        var resp = await _http.PutAsJsonAsync(
            $"/api/v1/organizations/{org}/routing-rules",
            new
            {
                version = "routing_rules_v1",
                rules = new
                {
                    bogus = new[] { new { model = "m" } },
                },
            });
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rules_Put_Then_Get_Roundtrip_And_Clear()
    {
        var org = DefaultOrgId();
        var resp = await _http.PutAsJsonAsync(
            $"/api/v1/organizations/{org}/routing-rules",
            new
            {
                version = "routing_rules_v1",
                rules = new
                {
                    code = new object[]
                    {
                        new
                        {
                            model = "gpt-5", provider = "openai",
                            budget_tier = "cheap",
                        },
                        new { model = "claude-opus", weight = 3 },
                    },
                },
                allowed_models = new[] { "gpt-5", "claude-opus" },
            });
        resp.StatusCode.ShouldBe(HttpStatusCode.NoContent,
            await resp.Content.ReadAsStringAsync());

        var doc = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/organizations/{org}/routing-rules");
        doc.GetProperty("rules").GetProperty("code")
            .GetArrayLength().ShouldBe(2);
        doc.GetProperty("allowed_models").GetArrayLength()
            .ShouldBe(2);

        // clear ⇒ vazio ⇒ pass-through
        var clear = await _http.PutAsJsonAsync(
            $"/api/v1/organizations/{org}/routing-rules",
            new { version = "routing_rules_v1" });
        clear.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var after = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/organizations/{org}/routing-rules");
        after.GetProperty("rules")
            .EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public async Task Rules_Member_Cannot_Read_Or_Write()
    {
        var org = DefaultOrgId();
        await using var db = Db();
        var member = new Infrastructure.Persistence.UserRecord
        {
            Id = Guid.NewGuid(),
            Email = "member149@local",
            DisplayName = "member149",
            RolesJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(member);
        db.OrgMembers.Add(new Infrastructure.Persistence.OrgMemberRecord
        {
            OrgId = org,
            UserId = member.Id,
            Role = "member",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        using var memberHttp = _factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                HandleCookies = true,
            });
        using var login = await memberHttp.PostAsJsonAsync(
            "/api/v1/auth/dev-login",
            new { email = "member149@local", displayName = "member149" });
        login.EnsureSuccessStatusCode();

        (await memberHttp.GetAsync(
            $"/api/v1/organizations/{org}/routing-rules"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await memberHttp.PutAsJsonAsync(
            $"/api/v1/organizations/{org}/routing-rules",
            new { version = "routing_rules_v1" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Rules_Unknown_Org_Forbidden()
    {
        var resp = await _http.GetAsync(
            $"/api/v1/organizations/{Guid.NewGuid()}/routing-rules");
        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ---------- GET /api/v1/usage/llm ----------

    [Fact]
    public async Task LlmUsage_ByRole_Groups_TokenEvents()
    {
        var org = DefaultOrgId();
        await using var db = Db();
        var convId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Conversations.Add(new ConversationRecord
        {
            Id = convId,
            OrgId = org,
            CreatedByUserId = _userId.ToString(),
            SelectedRepository = "org/repo",
            ExecutionStatus = "READY",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Events.Add(new EventRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = convId,
            Kind = "TokenEvent",
            Source = "agent",
            TimestampTicks = now.UtcTicks,
            Payload = """{"kind":"TokenEvent","role":"code","model":"gpt-5","prompt_tokens":10,"completion_tokens":5,"cost_usd":0.01}""",
        });
        db.Events.Add(new EventRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = convId,
            Kind = "TokenEvent",
            Source = "agent",
            TimestampTicks = now.UtcTicks,
            Payload = """{"kind":"TokenEvent","role":"review","model":"claude","prompt_tokens":7,"completion_tokens":3}""",
        });
        await db.SaveChangesAsync();

        var resp = await _http.GetAsync("/api/v1/usage/llm?by=role");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK,
            await resp.Content.ReadAsStringAsync());
        var doc = await resp.Content
            .ReadFromJsonAsync<JsonElement>();

        doc.GetProperty("by").GetString().ShouldBe("role");
        var rows = doc.GetProperty("rows").EnumerateArray().ToList();
        rows.Count.ShouldBe(2);
        rows.ShouldContain(r =>
            r.GetProperty("role").GetString() == "code"
            && r.GetProperty("model").GetString() == "gpt-5"
            && r.GetProperty("tokens").GetInt64() == 15);
        rows.ShouldContain(r =>
            r.GetProperty("role").GetString() == "review"
            && r.GetProperty("tokens").GetInt64() == 10);
    }

    [Fact]
    public async Task LlmUsage_Unsupported_By_Returns400()
    {
        var resp = await _http.GetAsync("/api/v1/usage/llm?by=model");
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}