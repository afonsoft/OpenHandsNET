using System.Text.Json;
using OpenHandsNET.Infrastructure.Agent;
using Shouldly;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-15 RF-003: mesmo em always-allow, ações destrutivas exigem
/// aprovação humana — MCP host, escrita fora de /workspace, force-push,
/// rm recursivo fora do workspace, killers de sistema.
/// </summary>
public class AutonomyGuardrailTests
{
    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("ls -la", false)]
    [InlineData("rm -rf node_modules", false)]
    [InlineData("rm -rf /workspace/build", false)]
    [InlineData("rm -rf ./dist", false)]
    [InlineData("git push origin main", false)]
    [InlineData("git push --force origin main", true)]
    [InlineData("git push -f origin main", true)]
    [InlineData("git push --force-with-lease", true)]
    [InlineData("rm -rf /", true)]
    [InlineData("rm -rf /etc", true)]
    [InlineData("rm -rf ~", true)]
    [InlineData("rm -rf $HOME", true)]
    [InlineData("rm -rf ..", true)]
    [InlineData("rm -r /tmp/cache", true)]
    [InlineData("rm file.txt", false)]
    [InlineData("rm -f file.txt", false)]
    [InlineData("echo ok && rm -rf /var", true)]
    [InlineData("cat /etc/passwd | grep root; rm -rf /boot", true)]
    [InlineData("mkfs.ext4 /dev/sda", true)]
    [InlineData("dd if=/dev/zero of=/dev/sda", true)]
    [InlineData("shutdown -h now", true)]
    [InlineData("reboot", true)]
    [InlineData("chmod -R 777 /", true)]
    [InlineData("chmod -R 755 /workspace", false)]
    [InlineData(":(){ :|:& };:", true)]
    public void Bash_Destructive_Patterns(string command, bool expected)
    {
        var args = Args($"{{\"command\":{JsonSerializer.Serialize(command)}}}");
        AutonomyGuardrail.RequiresHumanApproval("bash", args)
            .ShouldBe(expected, $"command: {command}");
    }

    [Theory]
    [InlineData("file_write", "/workspace/src/a.cs", false)]
    [InlineData("file_write", "src/relative.cs", false)]
    [InlineData("file_write", "/etc/nginx.conf", true)]
    [InlineData("file_write", "../escape.cs", true)]
    [InlineData("file_edit", "/tmp/x", true)]
    [InlineData("file_edit", "/workspace/../out", true)]
    [InlineData("file_edit", "~/secret", true)]
    public void File_Tools_Outside_Workspace_Ask(
        string tool, string path, bool expected)
    {
        var args = Args($"{{\"path\":{JsonSerializer.Serialize(path)}}}");
        AutonomyGuardrail.RequiresHumanApproval(tool, args)
            .ShouldBe(expected);
    }

    [Fact]
    public void Mcp_Tools_Always_Ask()
    {
        AutonomyGuardrail.RequiresHumanApproval("mcp__github__create_pr", null)
            .ShouldBeTrue();
        AutonomyGuardrail.RequiresHumanApproval("mcp__fs__read", Args("{}"))
            .ShouldBeTrue();
    }

    [Theory]
    [InlineData("think")]
    [InlineData("file_read")]
    [InlineData("task_tracker")]
    [InlineData("finish")]
    [InlineData("git_commit")]
    [InlineData("browser")]
    public void Routine_Tools_Never_Ask(string tool) =>
        AutonomyGuardrail.RequiresHumanApproval(tool, Args("{}"))
            .ShouldBeFalse();

    [Fact]
    public void Missing_Args_Are_Safe()
    {
        AutonomyGuardrail.RequiresHumanApproval("bash", null).ShouldBeFalse();
        AutonomyGuardrail.RequiresHumanApproval("file_write", Args("{}"))
            .ShouldBeFalse();
    }
}
