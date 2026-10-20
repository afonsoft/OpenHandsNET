using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-43: contrato dos endpoints de checkpoint — 400 hash inválido,
/// 503 sem sandbox running, 404 sem repo, 409 run_active / drift,
/// e roundtrip Docker snapshot→edit→revert.
/// </summary>
public sealed class Spec43CheckpointEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-spec43-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec43CheckpointEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Database:Provider", "Sqlite");
            b.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
            b.UseSetting("Observability:OtlpEndpoint", string.Empty);
            b.UseSetting("Sandbox:DefaultImage", "bash:5");
            b.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            b.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"ohn-spec43-ws-{Guid.NewGuid():N}"));
        });
        _http = _factory.CreateClient();
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath)) { File.Delete(_dbPath); }
    }

    private async Task<Guid> SeedConversationAsync(string status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<OpenHandsNET.Infrastructure.Persistence.AppDbContext>();
        var conv = new OpenHandsNET.Infrastructure.Conversations.ConversationRecord
        {
            Id = Guid.NewGuid(),
            ExecutionStatus = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        return conv.Id;
    }

    [Fact]
    public async Task Hash_Validation_400()
    {
        var id = Guid.NewGuid();
        (await _http.GetAsync(
            $"/api/v1/app-conversations/{id}/checkpoints/notahash!"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{id}/checkpoints/x';rm/revert",
            new { force = true }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task No_Sandbox_503()
    {
        var id = await SeedConversationAsync("Finished");
        (await _http.GetAsync(
            $"/api/v1/app-conversations/{id}/checkpoints"))
            .StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{id}/checkpoints",
            new { label = "x" }))
            .StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Revert_409_Run_Active()
    {
        var id = await SeedConversationAsync("Running");
        var resp = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{id}/checkpoints/abcdef1/revert",
            new { force = true });
        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync())
            .ShouldContain("run_active");
    }

    // ---- roundtrip Docker --------------------------------------------------

    [Fact]
    public async Task Snapshot_Edit_Drift_Revert_Roundtrip()
    {
        if (!DockerUp()) { return; }

        var convId = await CreateReadyConversationAsync();
        var b = $"/api/v1/app-conversations/{convId}";
        // SPEC-78 RF-001: arquivo único por run — resíduo de workspace
        // reutilizado não pode contaminar snapshot/drift/revert.
        var file = $"a-{Guid.NewGuid():N}.txt";

        // READY pode chegar um tick antes do container virar Running —
        // sonda /exec até 200 (máx 30s).
        var probeDeadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < probeDeadline)
        {
            var probe = await _http.PostAsJsonAsync($"{b}/exec",
                new { command = "true" });
            if (probe.StatusCode == HttpStatusCode.OK)
            {
                break;
            }

            await Task.Delay(500);
        }

        // Imagem bash:5 não traz git — instala (Alpine) e faz init.
        // Se o egress do sandbox bloquear apk, o teste falha aqui.
        var init = await _http.PostAsJsonAsync($"{b}/exec", new
        {
            command = "mkdir -p /workspace/repo && " +
                "apk add --no-cache git >/dev/null 2>&1 && " +
                "cd /workspace/repo && git init -b main && " +
                "git -c user.email=t@t -c user.name=t " +
                "commit --allow-empty -qm init",
            timeout_s = 120,
        });
        init.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await init.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("exit_code").GetInt32().ShouldBe(0,
                "apk/git init deve rodar dentro do sandbox");

        // Arquivo inicial + snapshot.
        (await _http.PutAsJsonAsync($"{b}/file?path=/workspace/repo/{file}",
            new { content_base64 = B64("v1") }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var snap = await _http.PostAsJsonAsync($"{b}/checkpoints",
            new { label = "before-edit" });
        snap.StatusCode.ShouldBe(HttpStatusCode.OK);
        var ckpt = await snap.Content.ReadFromJsonAsync<JsonElement>();
        var hash = ckpt.GetProperty("hash").GetString()!;
        hash.ShouldMatch("^[0-9a-f]{7,64}$");

        // Lista contém o snapshot com stats.
        var list = await _http.GetFromJsonAsync<JsonElement>($"{b}/checkpoints");
        list.EnumerateArray().ShouldContain(
            e => e.GetProperty("hash").GetString() == hash
                && e.GetProperty("label").GetString() == "before-edit");

        // Edit pós-snapshot → drift.
        (await _http.PutAsJsonAsync($"{b}/file?path=/workspace/repo/{file}",
            new { content_base64 = B64("v2-CHANGED") }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Espera o run terminar — revert é 409 enquanto Running.
        await WaitRunFinishedAsync(convId);

        // Revert sem force → 409 com conflicts.
        var drift = await _http.PostAsJsonAsync(
            $"{b}/checkpoints/{hash}/revert", new { force = false });
        drift.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await drift.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("conflicts").EnumerateArray()
            .Select(e => e.GetString()).ShouldContain(file);

        // Revert com force → conteúdo volta a v1; pre-revert snapshot criado.
        var ok = await _http.PostAsJsonAsync(
            $"{b}/checkpoints/{hash}/revert", new { force = true });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var res = await ok.Content.ReadFromJsonAsync<JsonElement>();
        res.GetProperty("reverted").GetBoolean().ShouldBeTrue();
        res.GetProperty("pre_revert_hash").GetString()
            .ShouldNotBeNullOrEmpty();

        var read = await _http.GetFromJsonAsync<JsonElement>(
            $"{b}/file?path=/workspace/repo/{file}");
        read.GetProperty("content").GetString().ShouldBe("v1");
    }

    /// <summary>Poll até <c>execution_status != Running</c> (máx 30s).</summary>
    private async Task WaitRunFinishedAsync(string convId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var conv = await _http.GetFromJsonAsync<JsonElement>(
                $"/api/v1/app-conversations/{convId}");
            var st = conv.TryGetProperty("execution_status", out var e)
                ? e.GetString() : null;
            // DTO uppercase: RUNNING/STARTING/FINISHED…
            if (st is not null
                && !st.Equals("Running", StringComparison.OrdinalIgnoreCase)
                && !st.Equals("Starting", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(500);
        }
    }

    private static string B64(string s) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

    private async Task<string> CreateReadyConversationAsync()
    {
        var resp = await _http.PostAsJsonAsync("/api/v1/app-conversations", new
        {
            initial_message = new
            {
                role = "user",
                content = new[] { new { type = "text", text = "hi" } },
                // run default — aguardamos Finished antes do revert.
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