using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using OpenHandsNET.Application.Hooks;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Domain.Hooks;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Hooks;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-127: <see cref="WorkspaceHookService"/> — discovery em camadas
/// (repo antes de user), matcher glob, command hooks via sandbox,
/// blocking opt-in vetoando o passo, prompt hooks injetando mensagem,
/// e evento <c>hook_fired</c> por execução.
/// </summary>
public sealed class WorkspaceHookServiceTests
{
    private const string Dump = """
        ===repo:fmt.hook.json
        {"event":"on_tool_pre","matcher":"file_*","type":"command","command":"dotnet format","blocking":true,"timeout":30}
        ===repo:warn.hook.json
        {"event":"on_tool_post","type":"prompt","prompt":"remember to update tests"}
        ===user:audit.hook.json
        {"event":"PreToolUse","type":"command","command":"echo audit"}
        """;

    private static WorkspaceHookService Sut(FakeEventService events) =>
        new(events, NullLogger<WorkspaceHookService>.Instance);

    private static ToolCallCtx Ctx(
        FakeEventService events, FakeSandboxExecutor exec, string? tool)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ISandboxExecutor>(exec);
        return new ToolCallCtx(new HookContext
        {
            ConversationId = Guid.NewGuid(),
            Sandbox = new Sandbox(Guid.NewGuid(), Guid.Empty, null, "fake",
                SandboxStatus.Running, null, DateTimeOffset.UtcNow, null,
                null),
            Services = sc.BuildServiceProvider(),
            ToolName = tool,
        });
    }

    private sealed record ToolCallCtx(HookContext Hook);

    [Fact]
    public void ParseDump_Layers_Repo_Then_User()
    {
        var hooks = Sut(new FakeEventService()).ParseDump(Dump);
        hooks.Count.ShouldBe(3);
        hooks[0].Layer.ShouldBe("repo");
        hooks[0].Matcher.ShouldBe("file_*");
        hooks[0].Blocking.ShouldBeTrue();
        hooks[0].TimeoutSeconds.ShouldBe(30);
        hooks[2].Layer.ShouldBe("user");
        // alias OWUIC → nome canônico.
        hooks[2].Event.ShouldBe("on_tool_pre");
    }

    [Fact]
    public void ParseHook_Malformed_Skipped()
    {
        var hooks = Sut(new FakeEventService()).ParseDump(
            "===repo:bad.hook.json\n{not json}\n"
            + "===repo:noevent.hook.json\n{\"command\":\"x\"}\n");
        hooks.ShouldBeEmpty();
    }

    [Fact]
    public void GlobMatching()
    {
        WorkspaceHookService.GlobMatches("file_*", "file_read")
            .ShouldBeTrue();
        WorkspaceHookService.GlobMatches("file_*", "bash")
            .ShouldBeFalse();
        WorkspaceHookService.GlobMatches(null, "bash").ShouldBeTrue();
        WorkspaceHookService.GlobMatches("file_*", null).ShouldBeFalse();
    }

    [Fact]
    public async Task Command_Blocking_Failure_Vetoes_Step()
    {
        var events = new FakeEventService();
        var exec = new FakeSandboxExecutor
        {
            ExecHandler = cmd => cmd.Contains("*.hook.json")
                ? new CommandResult(0, Dump, "", false)
                : new CommandResult(1, "", "boom", false),
        };
        var ctx = Ctx(events, exec, "file_edit").Hook;

        var blocked = await Sut(events).RunAsync(
            HookEvent.OnToolPre, ctx, CancellationToken.None);

        blocked.ShouldBeTrue();
        var fired = events.Events.OfType<HookFiredEvent>().ToList();
        fired.Count.ShouldBe(2); // fmt (matcher hit) + audit (sem matcher)
        fired[0].Success.ShouldBeFalse();
        fired[0].Blocking.ShouldBeTrue();
        fired[0].HookName.ShouldBe("fmt.hook.json");
    }

    [Fact]
    public async Task Matcher_Miss_Skips_Command_Hook()
    {
        var events = new FakeEventService();
        var exec = new FakeSandboxExecutor
        {
            ExecHandler = cmd => cmd.Contains("*.hook.json")
                ? new CommandResult(0, Dump, "", false)
                : new CommandResult(0, "ok", "", false),
        };

        var blocked = await Sut(events).RunAsync(
            HookEvent.OnToolPre, Ctx(events, exec, "bash").Hook,
            CancellationToken.None);

        blocked.ShouldBeFalse();
        // fmt não casa (matcher file_* vs bash); audit roda.
        exec.Commands.Count(c => c.Contains("dotnet format"))
            .ShouldBe(0);
        events.Events.OfType<HookFiredEvent>().Single().HookName
            .ShouldBe("audit.hook.json");
    }

    [Fact]
    public async Task Prompt_Hook_Injects_Message_And_Fires()
    {
        var events = new FakeEventService();
        var exec = new FakeSandboxExecutor
        {
            ExecHandler = _ => new CommandResult(0, Dump, "", false),
        };

        var blocked = await Sut(events).RunAsync(
            HookEvent.OnToolPost, Ctx(events, exec, "file_edit").Hook,
            CancellationToken.None);

        blocked.ShouldBeFalse();
        var msg = events.Events.OfType<MessageEvent>().Single();
        msg.Source.ShouldBe(EventSource.Hook);
        msg.LlmMessage.GetRawText().ShouldContain("update tests");
        events.Events.OfType<HookFiredEvent>().Single().HookType
            .ShouldBe("prompt");
    }

    [Fact]
    public async Task No_Hooks_No_Events()
    {
        var events = new FakeEventService();
        var exec = new FakeSandboxExecutor
        {
            ExecHandler = _ => new CommandResult(0, "", "", false),
        };

        (await Sut(events).RunAsync(HookEvent.OnToolPre,
            Ctx(events, exec, "bash").Hook, CancellationToken.None))
            .ShouldBeFalse();
        events.Events.ShouldBeEmpty();
    }
}