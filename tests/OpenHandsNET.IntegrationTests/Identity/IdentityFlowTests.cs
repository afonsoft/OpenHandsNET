using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Infrastructure.Persistence;

using Shouldly;

namespace OpenHandsNET.IntegrationTests.Identity;

/// <summary>
/// SPEC-08 acceptance flows: dev-login cookie, API key binding + effective
/// org, permission 403s, super-admin invariant, device flow, invitations,
/// suspended org fallback, audit trail.
/// </summary>
public sealed class IdentityFlowTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-id-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public IdentityFlowTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath};Default Timeout=10");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:DefaultImage", "bash:5");
            builder.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            builder.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"ohn-id-ws-{Guid.NewGuid():N}"));
            builder.UseSetting("Auth:Provider", "none");
            builder.UseSetting("Auth:UserProvisioningEnabled", "true");
            builder.UseSetting("MultiTenancy:Enabled", "true");
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

    private static async Task<HttpClient> DevLoginAsync(
        HttpClient http, string email)
    {
        var res = await http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email, displayName = email.Split('@')[0] });
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        return http;
    }

    private async Task<(HttpClient http, JsonElement user)> LoginUserAsync(string email)
    {
        var http = NewClient();
        await DevLoginAsync(http, email);
        var auth = await http.PostAsJsonAsync("/api/v1/authenticate", new { });
        auth.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = await auth.Content.ReadFromJsonAsync<JsonElement>();
        return (http, doc.GetProperty("user"));
    }

    [Fact]
    public async Task First_User_Is_SuperAdmin_With_Default_Org()
    {
        var (_, user) = await LoginUserAsync("owner@example.com");
        user.GetProperty("is_super_admin").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Authenticate_Returns_User_Orgs_IdpType()
    {
        var (http, _) = await LoginUserAsync("a@example.com");
        var res = await http.PostAsJsonAsync("/api/v1/authenticate", new { });
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("user").GetProperty("email").GetString()
            .ShouldBe("a@example.com");
        doc.GetProperty("orgs").GetArrayLength().ShouldBe(1);
        doc.GetProperty("idp_type").GetString().ShouldBe("local");
        doc.GetProperty("auth_type").GetString().ShouldBe("cookie");
    }

    [Fact]
    public async Task ApiKey_Create_Current_And_Bearer_Only()
    {
        var (http, user) = await LoginUserAsync("keyuser@example.com");
        var created = await http.PostAsJsonAsync("/api/v1/keys", new
        {
            name = "ci key",
            org_id = Guid.Parse(user.GetProperty("current_org_id").GetString()!),
        });
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var key = await created.Content.ReadFromJsonAsync<JsonElement>();
        var plaintext = key.GetProperty("key").GetString()!;
        plaintext.ShouldStartWith("sk-");
        key.GetProperty("prefix").GetString().ShouldBe(plaintext[..12]);

        // Bearer auth → /keys/current with bound org.
        var anon = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        anon.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", plaintext);
        var current = await anon.GetAsync("/api/v1/keys/current");
        current.StatusCode.ShouldBe(HttpStatusCode.OK);
        var info = await current.Content.ReadFromJsonAsync<JsonElement>();
        info.GetProperty("auth_type").GetString().ShouldBe("bearer");
        info.GetProperty("bound_org_id").GetString()
            .ShouldBe(user.GetProperty("current_org_id").GetString());
        info.GetProperty("org_id").GetString()
            .ShouldBe(user.GetProperty("current_org_id").GetString());

        // Cookie session is NOT Bearer — /keys/current must reject (RF-002).
        var cookieCurrent = await http.GetAsync("/api/v1/keys/current");
        cookieCurrent.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Member_Without_Permission_Gets_403_With_Wire_Name()
    {
        var (admin, _) = await LoginUserAsync("admin@example.com");
        var orgId = await DefaultOrgIdAsync(admin);

        // second user invited as member
        var invite = await admin.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/members/invite",
            new { emails = new[] { "member@example.com" }, role = "member" });
        invite.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await invite.Content.ReadFromJsonAsync<JsonElement>())[0]
            .GetProperty("token").GetString()!;

        var (member, _) = await LoginUserAsync("member@example.com");
        var accept = await member.PostAsJsonAsync(
            "/api/v1/organizations/members/invite/accept", new { token });
        accept.StatusCode.ShouldBe(HttpStatusCode.OK);

        // member lacks create_organization (super-only) → 403 with wire name.
        var denied = await member.PostAsJsonAsync("/api/v1/organizations",
            new { name = "evil org" });
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await denied.Content.ReadAsStringAsync())
            .ShouldContain("Missing required permission: create_organization");
    }

    [Fact]
    public async Task Admin_Cannot_Invite_As_Owner()
    {
        var (owner, _) = await LoginUserAsync("o@example.com");
        var orgId = await DefaultOrgIdAsync(owner);

        var invite = await owner.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/members/invite",
            new { emails = new[] { "adm@example.com" }, role = "admin" });
        var token = (await invite.Content.ReadFromJsonAsync<JsonElement>())[0]
            .GetProperty("token").GetString()!;
        var (admin, _) = await LoginUserAsync("adm@example.com");
        await (await admin.PostAsJsonAsync(
            "/api/v1/organizations/members/invite/accept", new { token }))
            .ShouldBeOkAsync();

        var forbidden = await admin.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/members/invite",
            new { emails = new[] { "x@example.com" }, role = "owner" });
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Bound_Key_Cannot_Cross_Orgs()
    {
        var (owner, _) = await LoginUserAsync("b@example.com");
        var orgA = await DefaultOrgIdAsync(owner);
        var orgBRes = await owner.PostAsJsonAsync("/api/v1/organizations",
            new { name = "Org B" });
        var orgB = (await orgBRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        // Key bound to org B.
        var created = await owner.PostAsJsonAsync("/api/v1/keys",
            new { name = "bound", org_id = orgB });
        var plaintext = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("key").GetString()!;

        var bearer = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        bearer.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", plaintext);

        // Route org A + key bound to B → 403 (binding check before permission).
        var denied = await bearer.GetAsync(
            $"/api/v1/organizations/{orgA}");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Route org B + key bound to B → allowed.
        var allowed = await bearer.GetAsync(
            $"/api/v1/organizations/{orgB}");
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Last_SuperAdmin_Cannot_Be_Removed()
    {
        var (admin, user) = await LoginUserAsync("solo@example.com");
        var uid = user.GetProperty("id").GetGuid();
        var res = await admin.DeleteAsync($"/api/v1/admin/super-admins/{uid}");
        res.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Grant_Then_Revoke_SuperAdmin_Works_And_Audits()
    {
        var (admin, _) = await LoginUserAsync("root@example.com");
        var (_, second) = await LoginUserAsync("second@example.com");
        var uid = second.GetProperty("id").GetGuid();

        var grant = await admin.PostAsJsonAsync("/api/v1/admin/super-admins",
            new { user_id = uid });
        grant.StatusCode.ShouldBe(HttpStatusCode.OK);

        var revoke = await admin.DeleteAsync($"/api/v1/admin/super-admins/{uid}");
        revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var audit = await admin.GetFromJsonAsync<JsonElement>(
            "/api/v1/admin/audit?action=super_admin.revoke");
        audit.GetProperty("items").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Device_Flow_End_To_End_Issues_7Day_Key()
    {
        var http = NewClient();

        var auth = await http.PostAsync("/oauth/device/authorize", null);
        auth.StatusCode.ShouldBe(HttpStatusCode.OK);
        var device = await auth.Content.ReadFromJsonAsync<JsonElement>();
        var deviceCode = device.GetProperty("device_code").GetString()!;
        var userCode = device.GetProperty("user_code").GetString()!;
        device.GetProperty("expires_in").GetInt32().ShouldBe(600);
        device.GetProperty("interval").GetInt32().ShouldBe(5);

        // Pending poll.
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["device_code"] = deviceCode,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
        });
        var pending = await http.PostAsync("/oauth/device/token", form);
        pending.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await pending.Content.ReadAsStringAsync())
            .ShouldContain("authorization_pending");

        // Approve from an authenticated cookie session.
        var (admin, _) = await LoginUserAsync("approver@example.com");
        var verify = await admin.PostAsync(
            "/oauth/device/verify-authenticated",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["user_code"] = userCode,
                ["decision"] = "approve",
            }));
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Poll again → sk-* access token.
        var token = await http.PostAsync("/oauth/device/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["device_code"] = deviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }));
        token.StatusCode.ShouldBe(HttpStatusCode.OK);
        var accessToken = (await token.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
        accessToken.ShouldStartWith("sk-");

        var bearer = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        bearer.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", accessToken);
        (await bearer.GetAsync("/api/v1/keys/current")).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Suspended_Org_Blocks_Member_But_SuperAdmin_Passes()
    {
        var (owner, _) = await LoginUserAsync("s@example.com");
        var orgId = await DefaultOrgIdAsync(owner);

        // member joins
        var invite = await owner.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/members/invite",
            new { emails = new[] { "m2@example.com" }, role = "member" });
        var token = (await invite.Content.ReadFromJsonAsync<JsonElement>())[0]
            .GetProperty("token").GetString()!;
        var (member, _) = await LoginUserAsync("m2@example.com");
        await member.PostAsJsonAsync(
            "/api/v1/organizations/members/invite/accept", new { token });

        // suspend directly in DB
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var org = await db.Organizations.FindAsync(orgId);
            org!.SuspendedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        // member (org role) blocked on a perm-gated route
        var memberDenied = await member.GetAsync(
            $"/api/v1/organizations/{orgId}");
        memberDenied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // super admin fallback still reaches (view_org_settings isn't a super
        // perm → super admin also fails on org perms — but NOT blocked by
        // suspension on super-perm routes like manage_org_quota).
        var superOk = await owner.PutAsJsonAsync(
            $"/api/v1/organizations/{orgId}/quota",
            new { daily_max = 10, concurrent_max = 2 });
        superOk.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Provisioning_Creates_Login_User_And_Key()
    {
        var (admin, _) = await LoginUserAsync("prov@example.com");
        var orgId = await DefaultOrgIdAsync(admin);

        var res = await admin.PostAsJsonAsync(
            "/api/v1/organizations/provision-user", new
            {
                email = "newhire@example.com",
                name = "New Hire",
                org_id = orgId,
                role = "member",
                generate_password = true,
            });
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("email").GetString().ShouldBe("newhire@example.com");
        doc.GetProperty("password").GetString().ShouldNotBeNullOrEmpty();
        doc.GetProperty("api_key").GetString().ShouldStartWith("sk-");
    }

    [Fact]
    public async Task Audit_Log_Records_Login_And_KeyOps()
    {
        var (admin, _) = await LoginUserAsync("aud@example.com");
        await admin.PostAsJsonAsync("/api/v1/keys", new { name = "k1" });

        var audit = await admin.GetFromJsonAsync<JsonElement>(
            "/api/v1/admin/audit");
        var actions = audit.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("action").GetString())
            .ToList();
        actions.ShouldContain("auth.login");
        actions.ShouldContain("api_key.create");
        audit.GetProperty("total").GetInt32().ShouldBeGreaterThan(0);
    }

    private async Task<Guid> DefaultOrgIdAsync(HttpClient http)
    {
        var orgs = await http.GetFromJsonAsync<JsonElement>("/api/v1/organizations");
        return orgs![0].GetProperty("id").GetGuid();
    }
}

file static class HttpResponseAssertions
{
    public static async Task ShouldBeOkAsync(this HttpResponseMessage res) =>
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
}