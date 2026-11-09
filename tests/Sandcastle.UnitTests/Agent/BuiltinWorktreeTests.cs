using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Agent.Tools;

namespace Sandcastle.UnitTests.Agent;

/// <summary>
/// SPEC-73 (S3358): `worktree_diff` — a flag `--stat` sai do ternário
/// extraído (`statFlag`); estes testes travam a geração do comando.
/// </summary>
public sealed class BuiltinWorktreeTests
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

    private static FakeSandboxExecutor Exec() => new()
    {
        ExecHandler = _ => new CommandResult(0, "diff-out", "", false),
    };

    [Fact]
    public async Task Diff_StatOnly_Injeta_Stat_Flag()
    {
        var exec = Exec();
        var res = await BuiltinWorktree.WorktreeDiff().Handler(
            Ctx(exec), Args("""{"name":"feat","stat_only":true}"""),
            CancellationToken.None);

        res.IsError.ShouldBeFalse();
        exec.Commands.Single().ShouldContain("git diff --stat ");
    }

    [Fact]
    public async Task Diff_Full_Nao_Injeta_Stat_Flag()
    {
        var exec = Exec();
        var res = await BuiltinWorktree.WorktreeDiff().Handler(
            Ctx(exec), Args("""{"name":"feat"}"""),
            CancellationToken.None);

        res.IsError.ShouldBeFalse();
        var cmd = exec.Commands.Single();
        cmd.ShouldContain("git diff ");
        cmd.ShouldNotContain("--stat");
    }

    [Fact]
    public async Task Diff_BaseRef_Usa_Range_Explicito()
    {
        var exec = Exec();
        var res = await BuiltinWorktree.WorktreeDiff().Handler(
            Ctx(exec),
            Args("""{"name":"feat","base_ref":"main","stat_only":true}"""),
            CancellationToken.None);

        res.IsError.ShouldBeFalse();
        exec.Commands.Single().ShouldContain("git diff --stat 'main'...HEAD");
    }
}