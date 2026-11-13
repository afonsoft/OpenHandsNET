using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>
/// SPEC-150 — contrato dos endpoints de handoff: POST cria start-task
/// (async — nunca conversation_id), 400 p/ patch/base_sha/repo
/// inválidos ou oversize; GET package exige acesso (403), conversa
/// inexistente (404), sandbox morto sem snapshot (409) ou serve o
/// snapshot cacheado.
/// </summary>
public sealed class Spec150HandoffTests : IDisposable
{
    private const string Patch =
        "diff --git a/a.txt b/a.txt\n"
        + "index 1111111..2222222 100644\n"
        + "--- a/a.txt\n"
        + "+++ b/a.txt\n"
        + "@@ -1 +1 @@\n"
        + "-old\n"
        + "+new\n";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-spec150-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec150HandoffTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Database:Provider", "Sqlite");
                b.UseSetting("Database:ConnectionString",
                    $"Data Source={_dbPath};Default Timeout=10");
                b.UseSetting("Observability:OtlpEndpoint", string.Empty);
                b.UseSetting("Sandbox:DefaultImage", "bash:5");
                b.UseSetting("Sandbox:InstanceOwner",
                    $"test-{Guid.NewGuid():N}");
                b.UseSetting("Sandbox:WorkspaceRoot",
                    Path.Combine(Path.GetTempPath(),
                        $"sc-spec150-ws-{Guid.NewGuid():N}"));
            });
        _http = TestAuth.Login(_factory);
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

    private async Task<Guid> SeedConvAsync(
        string? owner = null, string? snapshot = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<
            Infrastructure.Persistence.AppDbContext>();
        var conv = new Infrastructure.Conversations.ConversationRecord
        {
            Id = Guid.NewGuid(),
            CreatedByUserId = owner,
            ExecutionStatus = "FINISHED",
            SelectedRepository = "o/r",
            HandoffPackageJson = snapshot,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        return conv.Id;
    }

    private async Task<string> MyUserIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<
            Infrastructure.Persistence.AppDbContext>();
        var user = await Microsoft.EntityFrameworkCore
            .EntityFrameworkQueryableExtensions
            .FirstAsync(db.Users, u => u.Email == "test@local");
        return user.Id.ToString();
    }

    // ------------------------------------------------------------- POST

    [Fact]
    public async Task Handoff_Create_Returns_StartTask_202()
    {
        var res = await _http.PostAsJsonAsync(
            "/api/v1/conversations/handoff", new
            {
                repository = "owner/repo",
                base_sha = "abc1234",
                base_branch = "main",
                diff_patch = Patch,
                message = "continue this work",
            });
        res.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().ShouldNotBeNullOrEmpty();
        // contrato async: task — nunca um conversation_id direto no POST.
        body.TryGetProperty("conversation_id", out _).ShouldBeFalse();
        body.GetProperty("status").GetString()
            .ShouldBe("WORKING");
    }

    [Theory]
    [InlineData("", "abc1234", Patch)]       // repository vazio
    [InlineData("o/r", "not-a-sha", Patch)]  // base_sha inválido
    [InlineData("o/r", "abc1234", "junk")]   // patch não-diff
    public async Task Handoff_Create_400_On_Invalid_Input(
        string repo, string sha, string patch)
    {
        var res = await _http.PostAsJsonAsync(
            "/api/v1/conversations/handoff", new
            {
                repository = repo,
                base_sha = sha,
                diff_patch = patch,
            });
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Handoff_Create_400_On_Oversize_Patch()
    {
        var big = Patch + new string('x', 600 * 1024);
        var res = await _http.PostAsJsonAsync(
            "/api/v1/conversations/handoff", new
            {
                repository = "o/r",
                base_sha = "abc1234",
                diff_patch = big,
            });
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // -------------------------------------------------------------- GET

    [Fact]
    public async Task Package_404_Unknown_Conversation()
    {
        var res = await _http.GetAsync(
            $"/api/v1/conversations/{Guid.NewGuid()}/handoff-package");
        res.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Package_403_Other_Owner()
    {
        // primeiro dev-user é superadmin (bootstrap) — o 403 precisa
        // de um segundo usuário sem papel/dono.
        var other = await TestAuth.LoginAsync(
            _factory, "other@local");
        try
        {
            var conv = await SeedConvAsync(
                owner: await MyUserIdAsync());
            var res = await other.GetAsync(
                $"/api/v1/conversations/{conv}/handoff-package");
            res.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            other.Dispose();
        }
    }

    [Fact]
    public async Task Package_409_Dead_Sandbox_No_Snapshot()
    {
        // sem sandbox vivo e sem snapshot → conflito (regenerate=1).
        var conv = await SeedConvAsync(
            owner: await MyUserIdAsync());
        var res = await _http.GetAsync(
            $"/api/v1/conversations/{conv}/handoff-package");
        res.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Package_200_From_Dead_Sandbox_Snapshot()
    {
        var snapshot = JsonSerializer.Serialize(new
        {
            repo = "o/r",
            head_branch = "work-x",
            head_sha = "h1",
            base_sha = "abc1234",
            diff_patch = Patch,
            summary = "agent did stuff",
            binary_files = Array.Empty<string>(),
            generated_at = DateTimeOffset.UtcNow.AddHours(-3),
        });
        var conv = await SeedConvAsync(
            owner: await MyUserIdAsync(), snapshot: snapshot);
        var res = await _http.GetAsync(
            $"/api/v1/conversations/{conv}/handoff-package");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("repo").GetString().ShouldBe("o/r");
        body.GetProperty("diff_patch").GetString().ShouldBe(Patch);
        body.GetProperty("summary").GetString()
            .ShouldBe("agent did stuff");
        body.TryGetProperty("generated_at", out _).ShouldBeTrue();
    }
}