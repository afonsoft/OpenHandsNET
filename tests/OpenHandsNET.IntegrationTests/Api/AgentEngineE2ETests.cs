using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-09 E2E: conversa com initial_message(run=true) → AgentRunner real +
/// MockLlm scriptado → bash tool dentro de sandbox Docker real → finish.
/// </summary>
public sealed class AgentEngineE2ETests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-agent-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public AgentEngineE2ETests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:DefaultImage", "bash:5");
            builder.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            builder.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"ohn-agent-ws-{Guid.NewGuid():N}"));
            builder.UseSetting("AgentLlm:Provider", "mock");
            builder.UseSetting("AgentLlm:AllowMock", "true");
            builder.UseSetting("AgentLlm:MockScript", """
                [{"tool":{"name":"bash","arguments":{"command":"echo hello-agent > /workspace/marker.txt && cat /workspace/marker.txt"}}},
                 {"text":"arquivo criado"},
                 {"tool":{"name":"finish","arguments":{"final_message":"tarefa concluída"}}}]
                """);
        });
        _http = _factory.CreateClient();
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static bool DockerUp()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("docker", "info")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    [Fact]
    public async Task InitialMessage_Run_Starts_Agent_Which_Finishes()
    {
        if (!DockerUp()) return;

        var resp = await _http.PostAsJsonAsync("/api/v1/app-conversations", new
        {
            initial_message = new
            {
                role = "user",
                content = new[] { new { type = "text", text = "crie um arquivo" } },
                run = true,
            },
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.Created);
        var task = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var convId = task.GetProperty("app_conversation_id").GetString()!;

        // Aguarda: READY → agente roda → ObservationEvent(bash) + finish.
        var deadline = DateTime.UtcNow.AddMinutes(4);
        var sawBash = false;
        var sawFinished = false;
        var lastDump = string.Empty;
        while (DateTime.UtcNow < deadline && !(sawBash && sawFinished))
        {
            var events = await _http.GetFromJsonAsync<JsonElement>(
                $"/api/v1/conversation/{convId}/events/search?limit=100");
            var kinds = new List<string>();
            foreach (var e in events.GetProperty("items").EnumerateArray())
            {
                var kind = e.GetProperty("kind").GetString() ?? "?";
                kinds.Add(kind);
                if (kind == "ObservationEvent"
                    && e.TryGetProperty("observation", out var obs)
                    && obs.ToString().Contains("hello-agent"))
                {
                    sawBash = true;
                }
                else if (kind == "ObservationEvent" || kind == "ServerErrorEvent")
                {
                    kinds[^1] = kind + ":" +
                        e.ToString()[..Math.Min(160, e.ToString().Length)];
                }

                if (kind == "ConversationStateUpdateEvent"
                    && e.TryGetProperty("value", out var v)
                    && v.ToString().Contains("finished"))
                {
                    sawFinished = true;
                }
            }

            lastDump = string.Join(" | ", kinds);
            if (!(sawBash && sawFinished))
            {
                await Task.Delay(1500);
            }
        }

        var convProbe = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/app-conversations/{convId}");
        var status = convProbe.GetProperty("execution_status").GetString();
        sawBash.ShouldBeTrue(
            $"bash observation missing; status={status}; events=[{lastDump}]");
        sawFinished.ShouldBeTrue(
            $"not finished; status={status}; events=[{lastDump}]");

        // O arquivo existe de verdade no workspace do sandbox.
        var conv = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/app-conversations/{convId}");
        conv.GetProperty("execution_status").GetString().ShouldBe("FINISHED");
        var file = await _http.GetAsync(
            $"/api/v1/app-conversations/{convId}/file?path=/workspace/marker.txt");
        (await file.Content.ReadAsStringAsync()).ShouldContain("hello-agent");
    }
}
