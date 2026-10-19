using OpenHandsNET.Domain.Sandbox;

namespace OpenHandsNET.UnitTests.SandboxOps;

/// <summary>SandboxProfiles: sizing tiers + resolution (SPEC-14 RF-001).</summary>
public sealed class SandboxProfilesTests
{
    [Theory]
    [InlineData("small", 2048, 1.0, 256)]
    [InlineData("medium", 4096, 2.0, 512)]
    [InlineData("large", 8192, 4.0, 1024)]
    public void Resolve_Returns_Expected_Limits(
        string name, int memoryMb, double cpu, int pids)
    {
        var p = SandboxProfiles.Resolve(name);
        p.ShouldNotBeNull();
        p.MemoryMb.ShouldBe(memoryMb);
        p.CpuLimit.ShouldBe(cpu);
        p.PidsLimit.ShouldBe(pids);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("huge")]
    [InlineData("SMALL")]
    public void Resolve_Unknown_Or_Invalid_Returns_Null(string? name) =>
        SandboxProfiles.Resolve(name).ShouldBeNull();

    [Theory]
    [InlineData("small", true)]
    [InlineData("medium", true)]
    [InlineData("large", true)]
    [InlineData("bogus", false)]
    [InlineData(null, false)]
    public void IsValid_Matches_Resolve(string? name, bool expected) =>
        SandboxProfiles.IsValid(name).ShouldBe(expected);

    [Fact]
    public void Default_Is_Medium() =>
        SandboxProfiles.Default.ShouldBe("medium");

    [Fact]
    public void Names_Covers_All_Tiers() =>
        SandboxProfiles.Names.ShouldBe(["small", "medium", "large"]);
}