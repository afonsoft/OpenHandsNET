using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenHandsNET.Infrastructure.Sandbox;

namespace OpenHandsNET.UnitTests.SandboxOps;

/// <summary>DockerNetworkPolicy: egress mode detection + env (SPEC-14 RF-002).</summary>
public sealed class DockerNetworkPolicyTests
{
    private static DockerNetworkPolicy Make(SandboxOptions opts) => new(
        docker: null!,
        Options.Create(opts),
        NullLogger<DockerNetworkPolicy>.Instance);

    [Theory]
    [InlineData("unrestricted", false)]
    [InlineData("Unrestricted", false)]
    [InlineData("allowlist", true)]
    [InlineData("deny", true)]
    public void IsRestricted_Matches_Mode(string mode, bool expected) =>
        Make(new SandboxOptions { EgressMode = mode })
            .IsRestricted.ShouldBe(expected);

    [Fact]
    public void EgressEnv_Unrestricted_Returns_Empty() =>
        Make(new SandboxOptions { EgressMode = "unrestricted" })
            .EgressEnv().ShouldBeEmpty();

    [Fact]
    public void EgressEnv_Deny_Exposes_Mode() =>
        Make(new SandboxOptions { EgressMode = "deny" })
            .EgressEnv().ShouldBe(["EGRESS_MODE=deny"]);

    [Fact]
    public void EgressEnv_Allowlist_Includes_Allow() =>
        Make(new SandboxOptions
        {
            EgressMode = "allowlist",
            EgressAllow = "api.github.com;*.corp",
        })
            .EgressEnv().ShouldBe(
                ["EGRESS_MODE=allowlist",
                 "EGRESS_ALLOWLIST=api.github.com;*.corp"]);

    [Fact]
    public void EgressEnv_Allowlist_Without_Allow_Omits_Var() =>
        Make(new SandboxOptions { EgressMode = "allowlist" })
            .EgressEnv().ShouldBe(["EGRESS_MODE=allowlist"]);

    [Fact]
    public void InternalNetworkName_Is_Stable() =>
        DockerNetworkPolicy.InternalNetworkName
            .ShouldBe("openhandsnet-egress-isolated");
}
