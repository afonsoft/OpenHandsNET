using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Application.Refactors;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Refactors;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>
/// SPEC-144: contrato dos endpoints de batch refactor —
/// <c>POST /api/v1/refactors</c> (202 + ids), validações 400, lista
/// com cursor, detalhe, batches (404 em plano órfão), gate de aprovação
/// no start, pause/resume/cancel e retry de batch.
/// </summary>
public sealed class Spec144RefactorEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-spec144-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec144RefactorEndpointsTests()
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

    private static object RefactorBody(
        IReadOnlyList<string>? repos = null,
        string goal = "migrate controllers to minimal APIs",
        string? strategy = null, string? mode = null,
        int? concurrency = null, int? sizeHint = null) => new
        {
            repositories = repos ?? new[] { "org/repo" },
            goal,
            strategy,
            mode,
            max_concurrency = concurrency,
            batch_size_hint = sizeHint,
        };

    private async Task<Guid> SeedPlanAsync(HttpClient http,
        string status = RefactorPlanStatuses.AwaitingApproval,
        string? planJson = "{\"schema\":\"refactor_plan_v1\"}")
    {
        // Recupera a org do usuário via primeira plan criada ou lista.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<AppDbContext>();
        var orgId = await db.OrgMembers
            .Select(m => m.OrgId).FirstOrDefaultAsync();
        var plan = new RefactorPlanRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            CreatedByUserId = Guid.NewGuid().ToString(),
            RepositoriesJson = "[\"org/repo\"]",
            Goal = "seeded plan",
            Strategy = RefactorStrategies.ImportGraph,
            Mode = RefactorModes.Independent,
            RequestedMode = RefactorModes.Independent,
            MaxConcurrency = 5,
            Status = status,
            PlanJson = planJson,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.RefactorPlans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    [Fact]
    public async Task Create_Returns_202_With_Planner_Ids()
    {
        var http = await LoginUserAsync("ref1@example.com");

        var res = await http.PostAsJsonAsync(
            "/api/v1/refactors", RefactorBody(strategy: "import-graph",
                mode: "independent", concurrency: 5, sizeHint: 20));

        res.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("plan_id").GetGuid()
            .ShouldNotBe(Guid.Empty);
        json.GetProperty("planner_conversation_id").GetGuid()
            .ShouldNotBe(Guid.Empty);
        json.GetProperty("status").GetString().ShouldBe("planning");
    }

    [Fact]
    public async Task Create_Validation_Errors_Are_400()
    {
        var http = await LoginUserAsync("ref2@example.com");

        (await http.PostAsJsonAsync("/api/v1/refactors",
            RefactorBody(strategy: "bogus"))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await http.PostAsJsonAsync("/api/v1/refactors",
            RefactorBody(mode: "bogus"))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await http.PostAsJsonAsync("/api/v1/refactors",
            RefactorBody(repos: []))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await http.PostAsJsonAsync("/api/v1/refactors",
            RefactorBody(goal: "  "))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await http.PostAsJsonAsync("/api/v1/refactors",
            RefactorBody(repos: new[] { "a/b", "c/d" },
                mode: "stacked"))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_Detail_Batches_And_404s()
    {
        var http = await LoginUserAsync("ref3@example.com");
        await http.PostAsJsonAsync(
            "/api/v1/refactors", RefactorBody(goal: "first"));
        var r2 = await http.PostAsJsonAsync(
            "/api/v1/refactors", RefactorBody(goal: "second"));
        var id2 = (await r2.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("plan_id").GetGuid();

        var det = await http.GetAsync($"/api/v1/refactors/{id2}");
        det.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detJson = await det.Content
            .ReadFromJsonAsync<JsonElement>();
        detJson.GetProperty("goal").GetString().ShouldBe("second");
        detJson.GetProperty("repositories")[0].GetString()
            .ShouldBe("org/repo");
        detJson.GetProperty("status").GetString()
            .ShouldBe("planning");

        var list = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/refactors?limit=1");
        list.GetProperty("items").GetArrayLength().ShouldBe(1);
        list.GetProperty("next_page_id").GetString().ShouldNotBeNull();

        // batches de um plano ainda em planning → lista vazia (200).
        var batches = await http.GetAsync(
            $"/api/v1/refactors/{id2}/batches");
        batches.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await batches.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").GetArrayLength().ShouldBe(0);

        (await http.GetAsync($"/api/v1/refactors/{Guid.NewGuid()}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await http.GetAsync(
            $"/api/v1/refactors/{Guid.NewGuid()}/batches")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Start_Approval_Pause_Resume_Cancel_Flow()
    {
        // dev-login user é owner da org pessoal → refactors.approve ok.
        var http = await LoginUserAsync("ref4@example.com");
        var planId = await SeedPlanAsync(http);

        var start = await http.PostAsync(
            $"/api/v1/refactors/{planId}/start", null);
        start.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var det = await http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/refactors/{planId}");
        det.GetProperty("status").GetString().ShouldBe("running");
        det.GetProperty("coordinator_conversation_id").GetGuid()
            .ShouldNotBe(Guid.Empty);

        var pause = await http.PostAsync(
            $"/api/v1/refactors/{planId}/pause", null);
        pause.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/refactors/{planId}"))
            .GetProperty("status").GetString().ShouldBe("paused");

        var resume = await http.PostAsync(
            $"/api/v1/refactors/{planId}/resume", null);
        resume.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var cancel = await http.PostAsync(
            $"/api/v1/refactors/{planId}/cancel", null);
        cancel.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/refactors/{planId}"))
            .GetProperty("status").GetString().ShouldBe("cancelled");

        // terminal → 409.
        (await http.PostAsync(
            $"/api/v1/refactors/{planId}/pause", null)).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Start_Before_Planning_Is_409()
    {
        var http = await LoginUserAsync("ref5@example.com");
        var res = await http.PostAsJsonAsync(
            "/api/v1/refactors", RefactorBody());
        var planId = (await res.Content
            .ReadFromJsonAsync<JsonElement>())
            .GetProperty("plan_id").GetGuid();

        (await http.PostAsync(
            $"/api/v1/refactors/{planId}/start", null)).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Batch_Retry_Contract()
    {
        var http = await LoginUserAsync("ref6@example.com");
        var planId = await SeedPlanAsync(http,
            status: RefactorPlanStatuses.Running);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<AppDbContext>();
        db.RefactorBatches.Add(new RefactorBatchRecord
        {
            Id = Guid.NewGuid(),
            PlanId = planId,
            N = 1,
            Repository = "org/repo",
            FilesJson = "[\"src/A.cs\"]",
            DependsOnJson = "[]",
            Risk = "low",
            VerifyCommand = "dotnet test",
            State = RefactorBatchStates.Failed,
            Error = "boom",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var retry = await http.PostAsync(
            $"/api/v1/refactors/{planId}/batches/1/retry", null);
        retry.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var batches = await http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/refactors/{planId}/batches");
        batches.GetProperty("items")[0].GetProperty("state")
            .GetString().ShouldBe("ready");
        batches.GetProperty("items")[0].GetProperty("n")
            .GetInt32().ShouldBe(1);

        // batch inexistente → 404.
        (await http.PostAsync(
            $"/api/v1/refactors/{planId}/batches/99/retry", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unauthenticated_Is_401()
    {
        var http = NewClient();
        (await http.PostAsJsonAsync("/api/v1/refactors",
            RefactorBody())).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await http.GetAsync("/api/v1/refactors")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }
}