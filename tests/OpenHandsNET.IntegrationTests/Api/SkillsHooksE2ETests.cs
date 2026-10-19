using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-11 E2E: conversa real (mock LLM + sandbox Docker) exercitando
/// injeção de skill no system prompt, tool invoke_skill e hooks de
/// ciclo de vida emitindo HookExecutionEvent.
/// </summary>
public sealed class SkillsHooksE2ETests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-ske2e-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public SkillsHooksE2ETests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:DefaultImage", "bash:5");
            builder.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            builder.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"ohn-ske2e-ws-{Guid.NewGuid():N}"));
            builder.UseSetting("AgentLlm:Provider", "mock");
            builder.UseSetting("AgentLlm:AllowMock", "true");
            builder.UseSetting("AgentLlm:MockScript", """
                [{"tool":{"name":"invoke_skill","arguments":{"name":"e2e-skill"}}},
                 {"tool":{"name":"invoke_skill","arguments":{"name":"missing-skill"}}},
                 {"tool":{"name":"finish","arguments":{"final_message":"done"}}}]
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
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    [Fact]
    public async Task Skills_Inject_InvokeSkill_Works_Hooks_Emit_Events()
    {
        if (!DockerUp()) return;

        // Skill user com trigger always → entra no system prompt.
        var skillResp = await _http.PostAsJsonAsync("/api/v1/skills", new
        {
            name = "e2e-skill",
            description = "e2e",
            content_md = "# E2E SKILL MARKER",
            triggers = new[] { new { type = "always", value = (string?)null } },
        });
        skillResp.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Hooks: emit_event em cada ponto coberto pelo run curto.
        foreach (var (ev, payload) in new[]
        {
            ("on_conversation_start", "started"),
            ("on_tool_post", "post-tool"),
            ("on_finish", "finished-hook"),
        })
        {
            (await _http.PostAsJsonAsync("/api/v1/hooks", new
            {
                name = $"hook-{ev}",
                @event = ev,
                action = "emit_event",
                payload,
                blocking = false,
            })).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var resp = await _http.PostAsJsonAsync("/api/v1/app-conversations", new
        {
            initial_message = new
            {
                role = "user",
                content = new[] { new { type = "text", text = "run e2e" } },
                run = true,
            },
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.Created);
        var convId = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("app_conversation_id").GetString()!;

        var deadline = DateTime.UtcNow.AddMinutes(3);
        var sawPromptSkill = false;
        var sawSkillContent = false;
        var sawMissingErr = false;
        var hookPoints = new HashSet<string>();
        var sawFinished = false;
        while (DateTime.UtcNow < deadline && !sawFinished)
        {
            var events = await _http.GetFromJsonAsync<JsonElement>(
                $"/api/v1/conversation/{convId}/events/search?limit=200");
            foreach (var e in events.GetProperty("items").EnumerateArray())
            {
                var kind = e.GetProperty("kind").GetString();
                switch (kind)
                {
                    case "SystemPromptEvent":
                        if (e.ToString().Contains("E2E SKILL MARKER"))
                            sawPromptSkill = true;
                        break;
                    case "ObservationEvent":
                        var obs = e.ToString();
                        if (obs.Contains("E2E SKILL MARKER")) sawSkillContent = true;
                        if (obs.Contains("missing-skill") && obs.Contains("not found"))
                            sawMissingErr = true;
                        break;
                    case "HookExecutionEvent":
                        hookPoints.Add(e.GetProperty("hook_event_type").GetString()!);
                        break;
                    case "ConversationStateUpdateEvent"
                        when e.ToString().Contains("finished"):
                        sawFinished = true;
                        break;
                }
            }

            if (!sawFinished) await Task.Delay(1500);
        }

        sawFinished.ShouldBeTrue("run should finish");
        sawPromptSkill.ShouldBeTrue("always skill should be in system prompt");
        sawSkillContent.ShouldBeTrue("invoke_skill should return content_md");
        sawMissingErr.ShouldBeTrue("missing skill should be a non-fatal error");
        hookPoints.ShouldContain("on_conversation_start");
        hookPoints.ShouldContain("on_tool_post");
        hookPoints.ShouldContain("on_finish");

        // conversation_skills persistidas no SETTING_UP_SKILLS.
        var convSkills = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/app-conversations/{convId}/skills");
        convSkills.EnumerateArray().Select(s => s.GetProperty("name").GetString())
            .ShouldContain("e2e-skill");
    }
}