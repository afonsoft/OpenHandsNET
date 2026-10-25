using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenHandsNET.IntegrationTests.Identity;

/// <summary>
/// SPEC-20 E2E: plan enforcement ligado → admin cria plano, atribui à org e
/// o create de conversa responde 403 plan_limit_exceeded. Também cobre
/// GET /organizations/{id}/plan.
/// </summary>
public sealed class PlanEnforcementEndpointTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-s20-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public PlanEnforcementEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath};Default Timeout=10");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:DefaultImage", "bash:5");
            builder.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            builder.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"ohn-s20-ws-{Guid.NewGuid():N}"));
            builder.UseSetting("Auth:Provider", "none");
            builder.UseSetting("Auth:UserProvisioningEnabled", "true");
            builder.UseSetting("MultiTenancy:Enabled", "true");
            builder.UseSetting("Billing:EnforcementEnabled", "true");
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

    private async Task<(HttpClient http, JsonElement user)> LoginAsync(string email)
    {
        var http = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true });
        var res = await http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email, displayName = email.Split('@')[0] });
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var auth = await http.PostAsJsonAsync("/api/v1/authenticate", new { });
        var doc = await auth.Content.ReadFromJsonAsync<JsonElement>();
        return (http, doc.GetProperty("user"));
    }

    private static Task<HttpResponseMessage> CreateConversationAsync(
        HttpClient http) => http.PostAsJsonAsync("/api/v1/app-conversations", new
        {
            initial_message = new
            {
                role = "user",
                content = new[] { new { type = "text", text = "hi" } },
                run = false,
            },
        });

    [Fact]
    public async Task Daily_Conversation_Limit_Returns_403_Contract()
    {
        // primeiro usuário: bootstrap super-admin + org pessoal.
        var (http, user) = await LoginAsync("admin@example.com");
        var orgId = user.GetProperty("current_org_id").GetString();

        // cria plano "tiny" com limite diário 1 e atribui à org.
        var create = await http.PostAsJsonAsync("/api/v1/admin/plans", new
        {
            name = "tiny",
            limits = new { max_conversations_per_day = 1 },
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var plan = await create.Content.ReadFromJsonAsync<JsonElement>();
        var planId = plan.GetProperty("id").GetString();

        var assign = await http.PutAsJsonAsync(
            $"/api/v1/admin/organizations/{orgId}/plan",
            new { plan_id = planId });
        assign.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // plano efetivo da org deve ser o "tiny".
        var eff = await http.GetAsync($"/api/v1/organizations/{orgId}/plan");
        eff.StatusCode.ShouldBe(HttpStatusCode.OK);
        var effDoc = await eff.Content.ReadFromJsonAsync<JsonElement>();
        effDoc.GetProperty("name").GetString().ShouldBe("tiny");
        effDoc.GetProperty("limits")
            .GetProperty("max_conversations_per_day").GetInt32().ShouldBe(1);

        // primeiro create passa, segundo estoura a quota diária.
        (await CreateConversationAsync(http)).StatusCode
            .ShouldBe(HttpStatusCode.Created);
        var blocked = await CreateConversationAsync(http);
        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var err = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        err.GetProperty("detail").GetString()
            .ShouldBe("plan_limit_exceeded");
        err.GetProperty("limit_key").GetString()
            .ShouldBe("max_conversations_per_day");
        err.GetProperty("limit").GetInt64().ShouldBe(1);
        err.GetProperty("used").GetInt64().ShouldBe(1);
    }

    [Fact]
    public async Task Plan_Delete_In_Use_Returns_409()
    {
        var (http, user) = await LoginAsync("sa2@example.com");
        var orgId = user.GetProperty("current_org_id").GetString();

        var create = await http.PostAsJsonAsync("/api/v1/admin/plans", new
        {
            name = "pro",
            limits = new { },
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var plan = await create.Content.ReadFromJsonAsync<JsonElement>();
        var planId = plan.GetProperty("id").GetString();

        (await http.PutAsJsonAsync(
            $"/api/v1/admin/organizations/{orgId}/plan",
            new { plan_id = planId }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // org.PlanId referencia o plano → delete 409.
        (await http.DeleteAsync($"/api/v1/admin/plans/{planId}"))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Plans_Endpoints_Require_SuperAdmin()
    {
        await LoginAsync("first@example.com"); // bootstrap super-admin
        var (pleb, _) = await LoginAsync("pleb2@example.com");
        (await pleb.GetAsync("/api/v1/admin/plans"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}