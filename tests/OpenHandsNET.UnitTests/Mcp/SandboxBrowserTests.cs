using OpenHandsNET.Domain.Mcp;
using OpenHandsNET.Infrastructure.Mcp;

namespace OpenHandsNET.UnitTests.Mcp;

/// <summary>
/// SPEC-131: env pass-through em <c>docker exec -e</c> (RF-002), merge do
/// servidor builtin <c>chrome-devtools</c> (RF-003) e invariantes da
/// imagem de runtime (RF-001, validação estática do Dockerfile).
/// </summary>
public sealed class SandboxBrowserTests
{
    private static McpServerConfig Cfg(
        IReadOnlyDictionary<string, string>? env = null) =>
        new()
        {
            Name = "chrome-devtools",
            Transport = McpTransport.Stdio,
            Command = "chrome-devtools-mcp",
            Args = ["--headless"],
            Env = env,
        };

    [Fact]
    public void DockerExecArgs_Passes_Env_Into_Container()
    {
        var args = McpClientService.DockerExecArgs(
            "ctr1", Cfg(new Dictionary<string, string>
            {
                ["PUPPETEER_EXECUTABLE_PATH"] = "/usr/local/bin/chromium",
            }));
        var i = args.IndexOf("-e");
        i.ShouldBeGreaterThanOrEqualTo(0);
        args[i + 1].ShouldBe(
            "PUPPETEER_EXECUTABLE_PATH=/usr/local/bin/chromium");
        args.IndexOf("ctr1").ShouldBeGreaterThan(i);
        args[^2..].ShouldBe(["chrome-devtools-mcp", "--headless"]);
    }

    [Fact]
    public void DockerExecArgs_No_Env_Plain_Exec()
    {
        var args = McpClientService.DockerExecArgs("c", Cfg());
        args.ShouldBe(["exec", "-i", "c", "chrome-devtools-mcp", "--headless"]);
    }

    [Fact]
    public void Builtin_ChromeDevtools_Merged_Unless_Overridden()
    {
        var merged = McpSettingsService.MergeBuiltin([]);
        merged.ShouldContain(c => c.Name == "chrome-devtools");

        var own = new McpServerConfig
        {
            Name = "chrome-devtools",
            Enabled = false,
        };
        merged = McpSettingsService.MergeBuiltin([own]);
        merged.Single(c => c.Name == "chrome-devtools").Enabled
            .ShouldBeFalse();

        var other = new McpServerConfig { Name = "github" };
        merged = McpSettingsService.MergeBuiltin([other]);
        merged.Select(c => c.Name).ShouldBe(["chrome-devtools", "github"]);
    }

    [Fact]
    public void Runtime_Dockerfile_Ships_Browser_Stack()
    {
        var root = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", ".."));
        var dockerfile = File.ReadAllText(
            Path.Combine(root, "runtime", "Dockerfile"));
        dockerfile.ShouldContain("PLAYWRIGHT_BROWSERS_PATH");
        dockerfile.ShouldContain("chrome-devtools-mcp");
        dockerfile.ShouldContain("playwright install --with-deps chromium");
        dockerfile.ShouldContain("PUPPETEER_EXECUTABLE_PATH");
    }
}