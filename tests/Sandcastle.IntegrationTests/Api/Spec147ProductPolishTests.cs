using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>
/// SPEC-147 API contract: <c>GET /api/v1/usage/budget</c> (RF-004) e
/// <c>GET /api/v1/indexing/status</c> (RF-005) — snake_case, escopo do
/// caller e contagem X/Y do agregado de indexação.
/// </summary>
public sealed class Spec147ProductPolishTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-147-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;
    private readonly Guid _userId;

    public Spec147ProductPolishTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath};Default Timeout=10");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:DefaultImage", "bash:5");
            builder.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            builder.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"sc-147-ws-{Guid.NewGuid():N}"));
            builder.UseEnvironment("Development");
        });
        var options = new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
        };
        _http = _factory.CreateClient(options);
        using var login = _http.PostAsJsonAsync(
            "/api/v1/auth/dev-login",
            new { email = "spec147@local", displayName = "spec147" })
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

    // ---------- RF-004: /api/v1/usage/budget ----------

    [Fact]
    public async Task Budget_ReturnsSnakeCaseContract()
    {
        var doc = await _http.GetFromJsonAsync<JsonElement>(
            "/api/v1/usage/budget");

        foreach (var key in new[]
        {
            "monthly_limit", "consumed", "remaining",
            "period_start", "period_end", "by_model",
        })
        {
            doc.TryGetProperty(key, out _).ShouldBeTrue(
                $"contract key {key} missing");
        }

        doc.GetProperty("consumed").ValueKind.ShouldBe(JsonValueKind.Number);
        doc.GetProperty("by_model").ValueKind.ShouldBe(JsonValueKind.Array);
        DateTimeOffset.Parse(doc.GetProperty("period_start").GetString()!)
            .Day.ShouldBe(1);
    }

    [Fact]
    public async Task Budget_InvalidOrg_Returns400()
    {
        var resp = await _http.GetAsync("/api/v1/usage/budget?org=nope");
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Budget_OrgView_NonMember_Forbidden()
    {
        var resp = await _http.GetAsync(
            $"/api/v1/usage/budget?org={Guid.NewGuid()}");
        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ---------- RF-005: /api/v1/indexing/status ----------

    [Fact]
    public async Task IndexingStatus_Empty_ReturnsZeroCounts()
    {
        var resp = await _http.GetAsync("/api/v1/indexing/status");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK,
            await resp.Content.ReadAsStringAsync());
        var doc = await resp.Content
            .ReadFromJsonAsync<JsonElement>();

        doc.GetProperty("total_connected").GetInt32().ShouldBe(0);
        doc.GetProperty("indexed").GetInt32().ShouldBe(0);
        doc.GetProperty("indexing").GetInt32().ShouldBe(0);
        doc.GetProperty("failed").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task IndexingStatus_CountsLatestPerRepo()
    {
        await using var db = Db();
        var now = DateTimeOffset.UtcNow;
        // dev-login bootstrapa uma default org — o escopo efetivo é org.
        var orgId = Guid.Parse(
            db.Users.Find(_userId)!.CurrentOrgId!);
        db.Conversations.Add(new ConversationRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            CreatedByUserId = _userId.ToString(),
            SelectedRepository = "org/repo-a",
            ExecutionStatus = "READY",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Conversations.Add(new ConversationRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            CreatedByUserId = _userId.ToString(),
            SelectedRepository = "org/repo-b",
            ExecutionStatus = "READY",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.RepoIndexes.Add(new RepoIndexRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Repository = "org/repo-a",
            Branch = "main",
            Status = "ready",
            CreatedAt = now,
            UpdatedAt = now,
        });
        // Record antigo "indexing" + recente "failed" — só o último conta.
        db.RepoIndexes.Add(new RepoIndexRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Repository = "org/repo-b",
            Branch = "main",
            Status = "indexing",
            CreatedAt = now.AddMinutes(-10),
            UpdatedAt = now.AddMinutes(-10),
        });
        db.RepoIndexes.Add(new RepoIndexRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Repository = "org/repo-b",
            Branch = "main",
            Status = "failed",
            CreatedAt = now,
            UpdatedAt = now,
        });
        // Repo indexado sem conversa no escopo — entra no total (org).
        db.RepoIndexes.Add(new RepoIndexRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Repository = "org/repo-c",
            Branch = "main",
            Status = "indexing",
            CreatedAt = now,
            UpdatedAt = now,
        });
        // Index de outra org — fora do escopo efetivo do caller.
        db.RepoIndexes.Add(new RepoIndexRecord
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            Repository = "org/other",
            Branch = "main",
            Status = "ready",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var resp = await _http.GetAsync("/api/v1/indexing/status");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK,
            await resp.Content.ReadAsStringAsync());
        var doc = await resp.Content
            .ReadFromJsonAsync<JsonElement>();

        doc.GetProperty("total_connected").GetInt32().ShouldBe(3);
        doc.GetProperty("indexed").GetInt32().ShouldBe(1);
        doc.GetProperty("indexing").GetInt32().ShouldBe(1);
        doc.GetProperty("failed").GetInt32().ShouldBe(1);
    }
}