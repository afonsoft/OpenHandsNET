using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Agent.Tools;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-126: `lsp_*` via daemon residente (call.py + socket) com
/// restart sob demanda e fallback one-shot transparente.
/// </summary>
public sealed class BuiltinLspDaemonTests
{
    private static readonly Domain.Sandbox.Sandbox TestSandbox = new(
        Guid.NewGuid(), Guid.Empty, null, "fake", SandboxStatus.Running,
        null, DateTimeOffset.UtcNow, null, null);

    private static ToolCallContext Ctx(FakeSandboxExecutor exec) => new(
        Guid.NewGuid(), TestSandbox, exec, new FakeEventService(),
        new ServiceCollection().BuildServiceProvider(),
        TimeSpan.FromSeconds(30));

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static Func<string, CommandResult> Script(
        Func<string, CommandResult?> route) =>
        cmd => route(cmd) ?? new CommandResult(0, "yes\n", "", false);

    private static int CountCalls(
        FakeSandboxExecutor exec, string marker) =>
        exec.Commands.Count(c => c.Contains(marker, StringComparison.Ordinal));

    [Fact]
    public async Task Daemon_Alive_Serves_Without_Restart()
    {
        var exec = new FakeSandboxExecutor
        {
            ExecHandler = Script(cmd =>
                cmd.Contains("call.py", StringComparison.Ordinal)
                    ? new CommandResult(0,
                        """{"bridge":"daemon","result":{"items":[]}}""",
                        "", false)
                    : null),
        };
        var res = await BuiltinLsp.LspDiagnostics().Handler(
            Ctx(exec), Args("""{"path":"src/a.py"}"""),
            CancellationToken.None);

        res.IsError.ShouldBeFalse();
        CountCalls(exec, "call.py").ShouldBe(1);
        CountCalls(exec, "serve").ShouldBe(0);
    }

    [Fact]
    public async Task Daemon_Dead_Restarts_And_Retries()
    {
        var calls = 0;
        var exec = new FakeSandboxExecutor
        {
            ExecHandler = Script(cmd =>
            {
                if (cmd.Contains("call.py", StringComparison.Ordinal))
                {
                    calls++;
                    return calls == 1
                        ? new CommandResult(5, "", "__NO_DAEMON__", false)
                        : new CommandResult(0,
                            """{"bridge":"daemon","result":{"items":[1]}}""",
                            "", false);
                }
                return null;
            }),
        };
        var res = await BuiltinLsp.LspDiagnostics().Handler(
            Ctx(exec), Args("""{"path":"src/a.py"}"""),
            CancellationToken.None);

        res.IsError.ShouldBeFalse();
        CountCalls(exec, "serve").ShouldBeGreaterThan(0);
        CountCalls(exec, "call.py").ShouldBe(2);
    }

    [Fact]
    public async Task Daemon_Unrecoverable_Falls_Back_OneShot()
    {
        var exec = new FakeSandboxExecutor
        {
            ExecHandler = Script(cmd =>
            {
                if (cmd.Contains("call.py", StringComparison.Ordinal))
                {
                    return new CommandResult(5, "", "__NO_DAEMON__", false);
                }
                if (cmd.Contains("bridge.py diagnostics", StringComparison.Ordinal))
                {
                    return new CommandResult(0,
                        """{"diagnostics":[]}""", "", false);
                }
                return null;
            }),
        };
        var res = await BuiltinLsp.LspDiagnostics().Handler(
            Ctx(exec), Args("""{"path":"src/a.py"}"""),
            CancellationToken.None);

        res.IsError.ShouldBeFalse();
        CountCalls(exec, "call.py").ShouldBe(2);
        CountCalls(exec, "bridge.py diagnostics").ShouldBe(1);
    }

    [Fact]
    public async Task Daemon_Error_Field_Propagates()
    {
        var exec = new FakeSandboxExecutor
        {
            ExecHandler = Script(cmd =>
                cmd.Contains("call.py", StringComparison.Ordinal)
                    ? new CommandResult(0,
                        """{"error":"sem servidor p/ '.xyz'"}""",
                        "", false)
                    : null),
        };
        var res = await BuiltinLsp.LspDiagnostics().Handler(
            Ctx(exec), Args("""{"path":"src/a.xyz"}"""),
            CancellationToken.None);
        res.IsError.ShouldBeTrue();
    }

    [Fact]
    public void Bridge_And_Call_Scripts_Compile()
    {
        if (!HasPython())
        {
            return; // python3 ausente no host de teste — skip inerte.
        }
        foreach (var (name, script) in ExtractScripts())
        {
            var tmp = Path.Combine(Path.GetTempPath(),
                $"ohn-lsp-{name}-{Guid.NewGuid():N}.py");
            File.WriteAllText(tmp, script);
            var p = Process.Start(new ProcessStartInfo(
                "python3", $"-m py_compile {tmp}")
            {
                RedirectStandardError = true,
            })!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            p.ExitCode.ShouldBe(0, $"{name} falhou py_compile: {err}");
        }
    }

    private static bool HasPython()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(
                "python3", "--version")
            {
                RedirectStandardOutput = true,
            });
            return p is not null && p.WaitForExit(10_000)
                && p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<(string Name, string Script)> ExtractScripts()
    {
        var t = typeof(BuiltinLsp);
        foreach (var name in new[] { "BridgeScript", "CallScript" })
        {
            var f = t.GetField(name,
                BindingFlags.NonPublic | BindingFlags.Static);
            var v = f?.GetValue(null) as string;
            v.ShouldNotBeNullOrEmpty($"{name} ausente");
            yield return (name, v!);
        }
    }
}
