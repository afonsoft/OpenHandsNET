using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Infrastructure.Persistence;

using Shouldly;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-50 RF-002 + DoD: contrato dos 4 endpoints
/// <c>/api/v1/keys/llm/byor*</c> — não são mais 403; 503 fail-closed
/// quando o proxy está off; refresh retorna a key <b>uma única vez</b>
/// e audita; delete revoga; permitted devolve a allowlist do proxy.
/// </summary>
public sealed class Spec50ByorEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-spec50-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeByorKeyService _byor = new();

    public Spec50ByorEndpointsTests()
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
                b.ConfigureServices(services => services.Replace(
                    ServiceDescriptor.Scoped<IByorKeyService>(
                        _ => _byor)));
            });
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_dbPath)) { File.Delete(_dbPath); }
    }

    private async Task<HttpClient> LoginAsync(string email)
    {
        var http = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true });
        var res = await http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email, displayName = email.Split('@')[0] });
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await http.PostAsJsonAsync("/api/v1/authenticate", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        return http;
    }

    [Fact]
    public async Task Get_Returns_Metadata_Never_Plaintext()
    {
        _byor.Active = new ByorKeyInfo(Guid.NewGuid(), "kid-1",
            "sk-litellm-ab12", 50m, ["gpt-4o-mini"], DateTimeOffset.UtcNow);
        var http = await LoginAsync("byor-get@example.com");

        var res = await http.GetAsync("/api/v1/keys/llm/byor");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("configured").GetBoolean().ShouldBeTrue();
        doc.GetProperty("key_prefix").GetString().ShouldBe("sk-litellm-ab12");
        doc.GetProperty("key_id").GetString().ShouldBe("kid-1");
        doc.GetProperty("budget").GetDecimal().ShouldBe(50m);
        doc.GetProperty("models").EnumerateArray()
            .Select(m => m.GetString()).ShouldBe(["gpt-4o-mini"]);
        doc.ToString().ShouldNotContain("sk-litellm-ab12-SECRET");
    }

    [Fact]
    public async Task Endpoints_503_When_Proxy_Disabled()
    {
        _byor.Enabled = false;
        var http = await LoginAsync("byor-off@example.com");

        foreach (var res in new[]
        {
            await http.GetAsync("/api/v1/keys/llm/byor"),
            await http.GetAsync("/api/v1/keys/llm/byor/permitted"),
            await http.PostAsJsonAsync("/api/v1/keys/llm/byor/refresh",
                new { }),
            await http.DeleteAsync("/api/v1/keys/llm/byor"),
        })
        {
            res.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await res.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("code").GetString()
                .ShouldBe("litellm_unavailable");
        }
    }

    [Fact]
    public async Task Refresh_Returns_Key_Once_And_Audits()
    {
        var http = await LoginAsync("byor-refresh@example.com");
        var res = await http.PostAsJsonAsync(
            "/api/v1/keys/llm/byor/refresh", new { budget = 75m });

        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("key").GetString().ShouldBe("sk-litellm-fresh");
        doc.GetProperty("key_prefix").GetString()
            .ShouldBe("sk-litellm-fresh");
        _byor.LastRefreshBudget.ShouldBe(75m);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AuditEvents.ShouldContain(e => e.Action == "byor_key.refresh");
    }

    [Fact]
    public async Task Permitted_Returns_Proxy_Model_List()
    {
        var http = await LoginAsync("byor-perm@example.com");
        var res = await http.GetAsync("/api/v1/keys/llm/byor/permitted");

        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("models").EnumerateArray()
            .Select(m => m.GetString())
            .ShouldBe(["gpt-4o-mini", "claude-sonnet-4"]);
    }

    [Fact]
    public async Task Delete_Revokes_Then_404_When_None()
    {
        _byor.Active = new ByorKeyInfo(Guid.NewGuid(), "kid-1",
            "sk-litellm-ab12", 50m, [], DateTimeOffset.UtcNow);
        var http = await LoginAsync("byor-del@example.com");

        (await http.DeleteAsync("/api/v1/keys/llm/byor")).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        (await http.DeleteAsync("/api/v1/keys/llm/byor")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Anonymous_Gets_401()
    {
        var anon = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        (await anon.GetAsync("/api/v1/keys/llm/byor")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>Lifecycle fake — LiteLLM real não existe no test host.</summary>
    private sealed class FakeByorKeyService : IByorKeyService
    {
        public bool Enabled { get; set; } = true;
        public ByorKeyInfo? Active;
        public decimal? LastRefreshBudget;
        public List<string> Permitted { get; } =
            ["gpt-4o-mini", "claude-sonnet-4"];

        public Task<ByorKeyInfo?> GetActiveAsync(
            string userId, CancellationToken ct) => Task.FromResult(Active);

        public Task<(string Key, ByorKeyInfo Info)> RefreshAsync(
            string userId, string? orgId, decimal? budgetUsd,
            CancellationToken ct)
        {
            LastRefreshBudget = budgetUsd;
            Active = new ByorKeyInfo(Guid.NewGuid(), "kid-new",
                "sk-litellm-fresh", budgetUsd ?? 50m, Permitted,
                DateTimeOffset.UtcNow);
            return Task.FromResult(("sk-litellm-fresh", Active));
        }

        public Task<bool> RevokeAsync(string userId, CancellationToken ct)
        {
            var had = Active is not null;
            Active = null;
            return Task.FromResult(had);
        }

        public Task<ByorCallContext?> ResolveCallContextAsync(
            string userId, CancellationToken ct) =>
            Task.FromResult<ByorCallContext?>(null);

        public Task<IReadOnlyList<string>> PermittedModelsAsync(
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Permitted);
    }
}