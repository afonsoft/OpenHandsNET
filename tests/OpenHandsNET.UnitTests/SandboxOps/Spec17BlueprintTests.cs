using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenHandsNET.Infrastructure.Sandbox;
using OpenHandsNET.Sandboxing;

namespace OpenHandsNET.UnitTests.SandboxOps;

/// <summary>
/// SPEC-17 unit coverage: <c>openhandsnet.yaml</c> parsing, SDK auto-detect,
/// warm-pool image targets and agent-image detection. All pure host-side
/// logic — no docker.
/// </summary>
public sealed class Spec17BlueprintTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"ohn-spec17-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Parse_Full_Yaml_Returns_All_Fields()
    {
        var spec = BlueprintService.Parse("""
            image: ghcr.io/openhandsnet/runtime:custom
            sdk: [ "dotnet:10", "node:22", "python" ]
            env: { DOTNET_CLI_TELEMETRY_OPTOUT: "1", FOO: bar }
            setup: .openhands/bootstrap.sh
            """);
        spec.ShouldNotBeNull();
        spec.Image.ShouldBe("ghcr.io/openhandsnet/runtime:custom");
        spec.Sdks.ShouldBe(new Dictionary<string, string>
        {
            ["dotnet"] = "10", ["node"] = "22", ["python"] = "",
        });
        spec.Env["DOTNET_CLI_TELEMETRY_OPTOUT"].ShouldBe("1");
        spec.Env["FOO"].ShouldBe("bar");
        spec.SetupScript.ShouldBe(".openhands/bootstrap.sh");
    }

    [Fact]
    public void Parse_Minimal_Yaml_Uses_Defaults()
    {
        var spec = BlueprintService.Parse("image: my/img:1");
        spec.ShouldNotBeNull();
        spec.SetupScript.ShouldBe(".openhands/setup.sh");
        spec.Sdks.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_Invalid_Yaml_Returns_Null()
    {
        BlueprintService.Parse("image: [unclosed").ShouldBeNull();
    }

    [Fact]
    public void DetectSdks_Dotnet_From_GlobalJson()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "global.json"),
            """{"sdk":{"version":"10.0.100"}}""");
        BlueprintService.DetectSdks(_dir)["dotnet"].ShouldBe("10");
    }

    [Fact]
    public void DetectSdks_Combined_Manifests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "app.csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(_dir, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, ".nvmrc"), "20.11.0\n");
        File.WriteAllText(Path.Combine(_dir, "requirements.txt"), "flask\n");
        File.WriteAllText(Path.Combine(_dir, "go.mod"), "module x\n\ngo 1.23\n");

        var sdks = BlueprintService.DetectSdks(_dir);
        sdks["dotnet"].ShouldBe("10");
        sdks["node"].ShouldBe("20");
        sdks["python"].ShouldBe("3.13");
        sdks["go"].ShouldBe("1.23");
        sdks.ShouldNotContainKey("java");
    }

    [Fact]
    public void DetectSdks_Empty_Repo_Returns_Empty()
    {
        Directory.CreateDirectory(_dir);
        BlueprintService.DetectSdks(_dir).ShouldBeEmpty();
    }

    [Fact]
    public void IsAgentImage_Matches_Prefix_List()
    {
        var o = new SandboxOptions { AgentImagePrefixes = "foo/img; openhandsnet/runtime" };
        o.IsAgentImage("openhandsnet/runtime:latest").ShouldBeTrue();
        o.IsAgentImage("ghcr.io/acme/foo/img:v2").ShouldBeTrue();
        o.IsAgentImage("nikolaik/python-nodejs").ShouldBeFalse();
    }

    [Fact]
    public void PoolTargets_Default_Plus_Per_Image()
    {
        var o = Options.Create(new SandboxOptions
        {
            WarmPoolSize = 2,
            DefaultImage = "img/default:1",
            WarmPoolImages = "img/a:3;img/b:1",
        });
        var pool = new WarmPoolService(null!, o,
            NullLogger<WarmPoolService>.Instance);
        var targets = pool.PoolTargets();
        targets.ShouldBe(new Dictionary<string, int>
        {
            ["img/default:1"] = 2, ["img/a"] = 3, ["img/b"] = 1,
        });
    }

    [Fact]
    public void PoolTargets_Disabled_Returns_Only_Extras()
    {
        var o = Options.Create(new SandboxOptions { WarmPoolImages = "img/a:2" });
        var pool = new WarmPoolService(null!, o,
            NullLogger<WarmPoolService>.Instance);
        pool.PoolTargets().ShouldBe(
            new Dictionary<string, int> { ["img/a"] = 2 });
    }
}
