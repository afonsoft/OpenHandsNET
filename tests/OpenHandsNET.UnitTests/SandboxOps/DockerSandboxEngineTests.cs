using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Sandboxing;

namespace OpenHandsNET.UnitTests.SandboxOps;

/// <summary>SPEC-86: HostConfig mapping — OCI runtime, limits, port publishing.</summary>
public sealed class DockerSandboxEngineTests
{
    private static EngineCreateRequest Request(bool agentImage = false) =>
        new(
            Name: "ohn-test",
            Image: "openhandsnet/runtime:test",
            Env: [],
            Binds: ["/host/ws:/workspace:rw"],
            Labels: new Dictionary<string, string>(),
            MemoryBytes: 4L * 1024 * 1024 * 1024,
            NanoCpus: 2_000_000_000,
            PidsLimit: 512,
            Cmd: null,
            WorkingDir: null,
            IsAgentImage: agentImage,
            WorkspaceConversationId: null);

    [Fact]
    public void BuildHostConfig_SetsRuntime_WhenConfigured()
    {
        var options = new SandboxOptions { DockerRuntime = "sysbox-runc" };

        var cfg = DockerSandboxEngine.BuildHostConfig(
            Request(), options, ["/a:/b"]);

        cfg.Runtime.ShouldBe("sysbox-runc");
    }

    [Fact]
    public void BuildHostConfig_LeavesRuntimeNull_WhenNotConfigured()
    {
        var options = new SandboxOptions(); // DockerRuntime default = null

        var cfg = DockerSandboxEngine.BuildHostConfig(
            Request(), options, ["/a:/b"]);

        cfg.Runtime.ShouldBeNull();
    }

    [Fact]
    public void BuildHostConfig_IgnoresWhitespaceRuntime()
    {
        var options = new SandboxOptions { DockerRuntime = "  " };

        var cfg = DockerSandboxEngine.BuildHostConfig(
            Request(), options, ["/a:/b"]);

        cfg.Runtime.ShouldBeNull();
    }

    [Fact]
    public void BuildHostConfig_AppliesLimitsAndBinds()
    {
        var options = new SandboxOptions();
        var request = Request();

        var cfg = DockerSandboxEngine.BuildHostConfig(
            request, options, ["/a:/b"]);

        cfg.Memory.ShouldBe(request.MemoryBytes);
        cfg.NanoCPUs.ShouldBe(request.NanoCpus);
        cfg.PidsLimit.ShouldBe(request.PidsLimit);
        cfg.Binds.ShouldBe(["/a:/b"]);
    }

    [Theory]
    [InlineData(4_000_000_000L, 2_000_000_000L, 2_000_000_000L)] // spec 4 CPU on a 2-CPU host
    [InlineData(2_000_000_000L, 8_000_000_000L, 2_000_000_000L)] // fits — untouched
    [InlineData(1_500_000_000L, 2_000_000_000L, 1_500_000_000L)] // fractional fits
    [InlineData(0L, 2_000_000_000L, 0L)]                        // 0 = unlimited, untouched
    [InlineData(-1L, 2_000_000_000L, -1L)]                      // negative = unset, untouched
    [InlineData(5_000_000_000L, long.MaxValue, 5_000_000_000L)] // unknown ceiling
    public void ClampNanoCpus_ClampsToHostCeiling(
        long requested, long ceiling, long expected)
    {
        DockerSandboxEngine.ClampNanoCpus(requested, ceiling).ShouldBe(expected);
    }

    [Fact]
    public void BuildHostConfig_PublishesAgentPort_OnlyForAgentImages()
    {
        var options = new SandboxOptions { PublishAgentPort = true };

        var agent = DockerSandboxEngine.BuildHostConfig(
            Request(agentImage: true), options, []);
        agent.PortBindings.ShouldNotBeNull();
        agent.PortBindings!.Keys.ShouldContain("8000/tcp");

        var plain = DockerSandboxEngine.BuildHostConfig(
            Request(agentImage: false), options, []);
        plain.PortBindings.ShouldBeNull();
    }
}