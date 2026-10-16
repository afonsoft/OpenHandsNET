using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-42 RF-001: contrato dos endpoints de workspace —
/// PUT /file (etag+409+5MB), files/{mkdir,rename,delete}, files/search
/// e git/commit. Guardas rodam antes do lookup do sandbox (400 sem Docker).
/// </summary>
public sealed class Spec42WorkspaceEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-spec42-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec42WorkspaceEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Database:Provider", "Sqlite");
            b.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
            b.UseSetting("Observability:OtlpEndpoint", string.Empty);
            b.UseSetting("Sandbox:DefaultImage", "bash:5");
            b.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            b.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"ohn-spec42-ws-{Guid.NewGuid():N}"));
        });
        _http = _factory.CreateClient();
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath)) { File.Delete(_dbPath); }
    }

    private static string B64(string s) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

    private async Task<Guid> SeedRunningConversationAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<OpenHandsNET.Infrastructure.Persistence.AppDbContext>();
        var conv = new OpenHandsNET.Infrastructure.Conversations.ConversationRecord
        {
            Id = Guid.NewGuid(), ExecutionStatus = "Running",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        return conv.Id;
    }

    // ---- PUT /file ------------------------------------------------------

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/workspace/../etc/passwd")]
    [InlineData("../repo/x")]
    [InlineData("repo/x")]
    public async Task Write_Rejects_Path_Outside_Workspace(string path)
    {
        var resp = await _http.PutAsJsonAsync(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/file" +
            $"?path={Uri.EscapeDataString(path)}",
            new { content_base64 = B64("x") });
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"path={path}");
    }

    [Fact]
    public async Task Write_Rejects_Invalid_Base64()
    {
        var resp = await _http.PutAsJsonAsync(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/file" +
            "?path=/workspace/repo/a.txt",
            new { content_base64 = "!!!not-base64!!!" });
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Write_Enforces_5MB_Cap()
    {
        var big = Convert.ToBase64String(new byte[5 * 1024 * 1024 + 1]);
        var resp = await _http.PutAsJsonAsync(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/file" +
            "?path=/workspace/repo/big.bin",
            new { content_base64 = big });
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Write_409_Without_Running_Sandbox()
    {
        var id = await SeedRunningConversationAsync();
        var resp = await _http.PutAsJsonAsync(
            $"/api/v1/app-conversations/{id}/file?path=/workspace/repo/a.txt",
            new { content_base64 = B64("hi") });
        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ---- files/{mkdir,rename,delete} ------------------------------------

    [Fact]
    public async Task Mkdir_Rejects_Traversal_And_409_No_Sandbox()
    {
        var bad = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/files/mkdir",
            new { path = "/workspace/../../tmp/evil" });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var id = await SeedRunningConversationAsync();
        var ok = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{id}/files/mkdir",
            new { path = "/workspace/repo/src" });
        ok.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Rename_Validates_Both_Paths()
    {
        foreach (var (from, to) in new[]
        {
            ("/workspace/repo/a", "/etc/b"),
            ("/workspace/../../x", "/workspace/repo/b"),
        })
        {
            var resp = await _http.PostAsJsonAsync(
                $"/api/v1/app-conversations/{Guid.NewGuid()}/files/rename",
                new { from, to });
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
                $"{from} -> {to}");
        }
    }

    [Fact]
    public async Task Delete_Rejects_Workspace_Root_And_Traversal()
    {
        foreach (var path in new[] { "/workspace", "/workspace/",
            "/workspace/repo/../../x" })
        {
            var resp = await _http.PostAsJsonAsync(
                $"/api/v1/app-conversations/{Guid.NewGuid()}/files/delete",
                new { path });
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
                $"path={path}");
        }
    }

    // ---- files/search ----------------------------------------------------

    [Fact]
    public async Task Search_Requires_Query_And_Clean_Glob()
    {
        var id = Guid.NewGuid();
        (await _http.GetAsync(
            $"/api/v1/app-conversations/{id}/files/search"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _http.GetAsync(
            $"/api/v1/app-conversations/{id}/files/search" +
            $"?q=x&glob={Uri.EscapeDataString("../../*.cs")}"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_409_Without_Running_Sandbox()
    {
        var id = await SeedRunningConversationAsync();
        (await _http.GetAsync(
            $"/api/v1/app-conversations/{id}/files/search?q=hello"))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ---- git/commit -------------------------------------------------------

    [Fact]
    public async Task Commit_Requires_Message_And_Clean_Files()
    {
        var id = Guid.NewGuid();
        (await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{id}/git/commit", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{id}/git/commit",
            new { message = "m", files = new[] { "../escape" } }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Commit_409_Without_Running_Sandbox()
    {
        var id = await SeedRunningConversationAsync();
        var resp = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{id}/git/commit",
            new { message = "wip" });
        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ---- roundtrip com Docker (ETag, conflito, search, ops) ----------------

    [Fact]
    public async Task Write_Read_Etag_Conflict_Roundtrip()
    {
        if (!DockerUp()) { return; }

        var convId = await CreateReadyConversationAsync();
        var file = "/workspace/repo/spec42.txt";

        // Write novo → etag; read devolve mesmo etag.
        var w = await _http.PutAsJsonAsync(
            $"/api/v1/app-conversations/{convId}/file?path={file}",
            new { content_base64 = B64("hello world") });
        w.StatusCode.ShouldBe(HttpStatusCode.OK);
        var etag = (await w.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("etag").GetString();
        etag.ShouldNotBeNullOrEmpty();

        var r = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/app-conversations/{convId}/file?path={file}");
        r.GetProperty("content").GetString().ShouldBe("hello world");
        r.GetProperty("etag").GetString().ShouldBe(etag);

        // ETag stale → 409; etag atual → 200 com novo etag.
        (await _http.PutAsJsonAsync(
            $"/api/v1/app-conversations/{convId}/file?path={file}",
            new { content_base64 = B64("v2"), etag = "stale" }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var w2 = await _http.PutAsJsonAsync(
            $"/api/v1/app-conversations/{convId}/file?path={file}",
            new { content_base64 = B64("v2"), etag });
        w2.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await w2.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("etag").GetString().ShouldNotBe(etag);
    }

    [Fact]
    public async Task File_Ops_And_Search_Roundtrip()
    {
        if (!DockerUp()) { return; }

        var convId = await CreateReadyConversationAsync();
        var b = "/api/v1/app-conversations/" + convId;

        (await _http.PostAsJsonAsync($"{b}/files/mkdir",
            new { path = "/workspace/repo/spec42dir" }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _http.PutAsJsonAsync(
            $"{b}/file?path=/workspace/repo/spec42dir/needle.txt",
            new { content_base64 = B64("alpha\nSPEC42_NEEDLE\nomega") }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await _http.PostAsJsonAsync($"{b}/files/rename",
            new { from = "/workspace/repo/spec42dir/needle.txt",
                  to = "/workspace/repo/spec42dir/renamed.txt" }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var search = await _http.GetFromJsonAsync<JsonElement>(
            $"{b}/files/search?q=SPEC42_NEEDLE&glob=*.txt");
        var match = search.GetProperty("matches").EnumerateArray().Single();
        match.GetProperty("path").GetString()
            .ShouldBe("/workspace/repo/spec42dir/renamed.txt");
        match.GetProperty("line").GetInt32().ShouldBe(2);
        match.GetProperty("col").GetInt32().ShouldBe(1);
        match.GetProperty("preview").GetString()!
            .ShouldContain("SPEC42_NEEDLE");

        (await _http.PostAsJsonAsync($"{b}/files/delete",
            new { path = "/workspace/repo/spec42dir" }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _http.GetAsync($"{b}/file" +
            "?path=/workspace/repo/spec42dir/renamed.txt"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<string> CreateReadyConversationAsync()
    {
        var resp = await _http.PostAsJsonAsync("/api/v1/app-conversations", new
        {
            initial_message = new
            {
                role = "user",
                content = new[] { new { type = "text", text = "hi" } },
            },
        });
        var task = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = task.GetProperty("id").GetString()!;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        JsonElement current = task;
        while (DateTime.UtcNow < deadline)
        {
            current = (await _http.GetFromJsonAsync<JsonElement>(
                $"/api/v1/app-conversations/start-tasks/{taskId}"))!;
            if (current.GetProperty("status").GetString() is "READY" or "ERROR")
            {
                break;
            }

            await Task.Delay(1000);
        }

        current.GetProperty("status").GetString().ShouldBe("READY",
            $"task detail: {current.GetProperty("detail").GetString()}");
        return current.GetProperty("app_conversation_id").GetString()!;
    }

    private static bool DockerUp()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("docker", "info")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
