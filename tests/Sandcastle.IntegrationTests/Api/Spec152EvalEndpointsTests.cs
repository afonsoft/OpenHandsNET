using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Infrastructure.Eval;
using Sandcastle.Infrastructure.Persistence;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>
/// SPEC-152 §API — contrato de <c>/api/v1/evals</c>: POST 202 + dedup,
/// validações 400 (suite inválida, estimate_required gate, max_tasks),
/// list/detail/results/cancel, estimate 200, scorecard 200 e
/// <c>perm:evals.run</c> (member → 403). Runs seeded via AppDbContext
/// ficam fora da fila do <see cref="EvalWorker"/> → cancel é
/// determinístico; runs criadas via POST drenam o worker — sandbox
/// indisponível vira verdict error (contrato segue estável).
/// </summary>
public sealed class Spec152EvalEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-spec152-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec152EvalEndpointsTests()
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
        _http = _factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                HandleCookies = true,
            });
        var res = _http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email = "admin152@local", displayName = "admin152" })
            .Result;
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath)) { File.Delete(_dbPath); }
    }

    private async Task<Guid> OrgIdAsync()
    {
        var auth = await _http.PostAsJsonAsync(
            "/api/v1/authenticate", new { });
        var doc = await auth.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("user")
            .GetProperty("current_org_id").GetGuid();
    }

    private async Task<Guid> SeedRunAsync(
        string status = "queued", string suite = "swe-lite",
        int n = 3, int resolved = 0, Guid? orgId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<AppDbContext>();
        var org = orgId ?? await OrgIdAsync();
        var run = new EvalRunRecord
        {
            Id = Guid.NewGuid(),
            OrgId = org,
            CreatedByUserId = Guid.NewGuid().ToString(),
            Suite = suite,
            NTasks = n,
            Model = "m1",
            Status = status,
            ResolvedN = resolved,
            ScorePct = n == 0 ? 0
                : Math.Round(100.0 * resolved / n, 1),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.EvalRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    [Fact]
    public async Task Create_SweLite_Returns_202()
    {
        var res = await _http.PostAsJsonAsync("/api/v1/evals",
            new { suite = "swe-lite", model = "m1", n = 2 });
        res.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("id").GetGuid().ShouldNotBe(Guid.Empty);
        doc.GetProperty("status").GetString().ShouldBe("queued");
        doc.GetProperty("n_tasks").GetInt32().ShouldBe(2);
        res.Headers.Location!.ToString()
            .ShouldContain("/api/v1/evals/");
    }

    [Fact]
    public async Task Create_Dedup_Same_Key_Returns_Same_Run()
    {
        var body = new { suite = "swe-lite", model = "m-dedup", n = 1 };
        var first = await _http.PostAsJsonAsync("/api/v1/evals", body);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var id1 = (await first.Content
            .ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        var second = await _http.PostAsJsonAsync("/api/v1/evals", body);
        var id2 = (await second.Content
            .ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        id2.ShouldBe(id1); // idempotente enquanto a run está ativa
    }

    [Fact]
    public async Task Create_Invalid_Suite_And_N_Return_400()
    {
        (await _http.PostAsJsonAsync("/api/v1/evals",
            new { suite = "bogus-suite", n = 1 })).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await _http.PostAsJsonAsync("/api/v1/evals",
            new { suite = "swe-lite", n = 0 })).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        // custom exige tasks_path.
        (await _http.PostAsJsonAsync("/api/v1/evals",
            new { suite = "custom", n = 1 })).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_EstimateGate_400_Then_Ack_202()
    {
        // Gate padrão 25: swe-lite tem ~20 tasks — custom jsonl
        // com 30 tasks força o gate (n ≤ tasks.Count).
        var jsonl = Path.Combine(Path.GetTempPath(),
            $"eval-tasks-{Guid.NewGuid():N}.jsonl");
        var lines = Enumerable.Range(1, 30).Select(i =>
            $$"""{"task_id":"g{{i}}","image":"img:1","prompt":"p","checker":{"cmd":"true"},"timeout_s":60}""");
        await File.WriteAllLinesAsync(jsonl, lines);
        try
        {
            var gated = await _http.PostAsJsonAsync("/api/v1/evals",
                new
                {
                    suite = "custom",
                    tasks_path = jsonl,
                    model = "m1",
                    n = 30,
                });
            gated.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var doc = await gated.Content
                .ReadFromJsonAsync<JsonElement>();
            doc.GetProperty("error").GetString()
                .ShouldBe("estimate_required");
            doc.GetProperty("estimate")
                .GetProperty("requires_acknowledgement")
                .GetBoolean().ShouldBeTrue();

            var ack = await _http.PostAsJsonAsync("/api/v1/evals",
                new
                {
                    suite = "custom",
                    tasks_path = jsonl,
                    model = "m1",
                    n = 30,
                    estimate_acknowledged = true,
                });
            ack.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }
        finally
        {
            File.Delete(jsonl);
        }
    }

    [Fact]
    public async Task List_Get_Results_Contract()
    {
        var runId = await SeedRunAsync(status: "completed",
            n: 3, resolved: 2);

        var list = await _http.GetAsync("/api/v1/evals");
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        var listDoc = await list.Content
            .ReadFromJsonAsync<JsonElement>();
        listDoc.GetProperty("items").EnumerateArray()
            .ShouldContain(r => r.GetProperty("id").GetGuid()
                == runId);

        var get = await _http.GetAsync($"/api/v1/evals/{runId}");
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        var runDoc = await get.Content
            .ReadFromJsonAsync<JsonElement>();
        runDoc.GetProperty("suite").GetString().ShouldBe("swe-lite");
        runDoc.GetProperty("resolved_n").GetInt32().ShouldBe(2);
        runDoc.GetProperty("score_pct").GetDouble()
            .ShouldBe(66.7, 0.05);

        var results = await _http.GetAsync(
            $"/api/v1/evals/{runId}/results");
        results.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await results.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").GetArrayLength().ShouldBe(0);

        (await _http.GetAsync($"/api/v1/evals/{Guid.NewGuid()}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _http.GetAsync(
            $"/api/v1/evals/{Guid.NewGuid()}/results"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cancel_Queued_202_Terminal_409()
    {
        var queued = await SeedRunAsync(status: "queued");
        var cancel = await _http.PostAsync(
            $"/api/v1/evals/{queued}/cancel", null);
        cancel.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // Recancel → 409 (terminal).
        (await _http.PostAsync(
            $"/api/v1/evals/{queued}/cancel", null)).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);

        var completed = await SeedRunAsync(status: "completed");
        (await _http.PostAsync(
            $"/api/v1/evals/{completed}/cancel", null)).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);
        (await _http.PostAsync(
            $"/api/v1/evals/{Guid.NewGuid()}/cancel", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Estimate_And_Scorecard_200()
    {
        var est = await _http.PostAsJsonAsync("/api/v1/evals/estimate",
            new { suite = "swe-lite", n = 5, model = "m1" });
        est.StatusCode.ShouldBe(HttpStatusCode.OK);
        var estDoc = await est.Content
            .ReadFromJsonAsync<JsonElement>();
        estDoc.GetProperty("n").GetInt32().ShouldBe(5);
        estDoc.GetProperty("avg_prompt_tokens").GetInt64()
            .ShouldBeGreaterThan(0);

        var score = await _http.GetAsync(
            "/api/v1/evals/scorecard?suite=swe-lite");
        score.StatusCode.ShouldBe(HttpStatusCode.OK);
        var scoreDoc = await score.Content
            .ReadFromJsonAsync<JsonElement>();
        scoreDoc.GetProperty("suite").GetString()
            .ShouldBe("swe-lite");
    }

    [Fact]
    public async Task Member_Without_Perm_Gets_403()
    {
        var org = await OrgIdAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<AppDbContext>();
        var member = new UserRecord
        {
            Id = Guid.NewGuid(),
            Email = "member152@local",
            DisplayName = "member152",
            RolesJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(member);
        db.OrgMembers.Add(new OrgMemberRecord
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
        var login = await memberHttp.PostAsJsonAsync(
            "/api/v1/auth/dev-login",
            new
            {
                email = "member152@local",
                displayName = "member152"
            });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);

        // member NÃO tem perm:evals.run por default (admin+ only).
        (await memberHttp.PostAsJsonAsync("/api/v1/evals",
            new { suite = "swe-lite", n = 1 })).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await memberHttp.GetAsync("/api/v1/evals")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await memberHttp.GetAsync("/api/v1/evals/scorecard"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}