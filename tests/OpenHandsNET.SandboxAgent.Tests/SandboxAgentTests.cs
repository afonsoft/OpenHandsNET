using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenHandsNET.SandboxAgent.Tests;

/// <summary>
/// SPEC-17 contract tests do openhandsnet-sandbox-agent: deferred-init,
/// session key auth, exec one-shot/persistente e files confinados ao workspace.
/// </summary>
public sealed class SandboxAgentTests
{
    private static WebApplicationFactory<SandboxAgentProgram> Factory(
        Dictionary<string, string?> settings, string workspace)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ohn-agent-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return new WebApplicationFactory<SandboxAgentProgram>().WithWebHostBuilder(b =>
        {
            foreach (var (k, v) in settings)
            {
                b.UseSetting(k, v);
            }

            b.UseSetting("OHN_WORKSPACE", workspace);
        });
    }

    private static readonly string Workspace = Path.Combine(
        Path.GetTempPath(), $"ohn-agent-{Guid.NewGuid():N}");

    [Fact]
    public async Task DeferredInit_Dormant_Until_Init_With_Key()
    {
        var factory = Factory(new()
        {
            ["OHN_DEFERRED_INIT"] = "true",
            ["OHN_INIT_KEY"] = "init-secret",
        }, Workspace);
        var http = factory.CreateClient();

        (await http.GetAsync("/alive")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Dormant: exec/files → 503.
        (await http.PostAsJsonAsync("/exec", new { command = "echo hi" }))
            .StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        // Init com key errada → 401; sem session_key → 400.
        (await http.PostAsJsonAsync("/api/init", new { session_key = "s1" }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var bad = new HttpRequestMessage(HttpMethod.Post, "/api/init")
        {
            Content = JsonContent.Create(new { session_key = "s1" }),
        };
        bad.Headers.Add("X-Init-API-Key", "wrong");
        (await http.SendAsync(bad)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var ok = new HttpRequestMessage(HttpMethod.Post, "/api/init")
        {
            Content = JsonContent.Create(new
            {
                session_key = "s1",
                env = new Dictionary<string, string> { ["OHN_T"] = "1" },
            }),
        };
        ok.Headers.Add("X-Init-API-Key", "init-secret");
        var init = await http.SendAsync(ok);
        init.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Ready: exec exige session key.
        (await http.PostAsJsonAsync("/exec", new { command = "echo hi" }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var exec = new HttpRequestMessage(HttpMethod.Post, "/exec")
        {
            Content = JsonContent.Create(new { command = "echo hi" }),
        };
        exec.Headers.Add("X-Session-Key", "s1");
        var res = await http.SendAsync(exec);
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("exit_code").GetInt32().ShouldBe(0);
        body.GetProperty("std_out").GetString()!.Trim().ShouldBe("hi");
    }

    [Fact]
    public async Task ColdStart_Ready_When_Session_Key_In_Env()
    {
        var factory = Factory(new()
        {
            ["OHN_SESSION_KEY"] = "k9",
        }, Workspace);
        var http = factory.CreateClient();

        // Re-init → 409.
        (await http.PostAsJsonAsync("/api/init", new { session_key = "x" }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var exec = new HttpRequestMessage(HttpMethod.Post, "/exec")
        {
            Content = JsonContent.Create(new
            {
                command = "exit 3",
            }),
        };
        exec.Headers.Add("X-Session-Key", "k9");
        var res = await http.SendAsync(exec);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("exit_code").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Exec_Timeout_And_Session_State()
    {
        var factory = Factory(new() { ["OHN_SESSION_KEY"] = "k" }, Workspace);
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Session-Key", "k");

        var slow = await http.PostAsJsonAsync("/exec",
            new { command = "sleep 5", timeout_seconds = 1 });
        (await slow.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("timed_out").GetBoolean().ShouldBeTrue();

        // Sessão preserva cwd/env entre comandos.
        await http.PostAsJsonAsync("/exec", new
        {
            command = "cd /tmp && export FOO=bar",
            session_id = "s1",
        });
        var cwd = await http.PostAsJsonAsync("/exec",
            new { command = "pwd && echo $FOO", session_id = "s1" });
        var body = await cwd.Content.ReadFromJsonAsync<JsonElement>();
        var stdout = body.GetProperty("std_out").GetString()!;
        stdout.ShouldContain("/tmp");
        stdout.ShouldContain("bar");
    }

    [Fact]
    public async Task Files_Confined_To_Workspace()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"ohn-fw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var factory = Factory(new() { ["OHN_SESSION_KEY"] = "k" }, ws);
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Session-Key", "k");

        (await http.PutAsync("/files/a/b.txt",
            new StringContent("hello"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await http.GetStringAsync("/files/a/b.txt")).ShouldBe("hello");

        // Escape fora do workspace — o router normaliza `..` (404) e, se o
        // path chegar ao handler, o confinamento bloqueia (400).
        var evil = await http.PutAsync("/files/%2e%2e/%2e%2e/etc/evil.txt",
            new StringContent("x"));
        ((int)evil.StatusCode).ShouldBeOneOf(400, 404);
        File.Exists("/etc/evil.txt").ShouldBeFalse();

        // Listagem do diretório.
        var list = await http.GetFromJsonAsync<JsonElement>("/files/a");
        list.EnumerateArray().ShouldContain(e =>
            e.GetProperty("path").GetString() == "/a/b.txt");
    }
}
