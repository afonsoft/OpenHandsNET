using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OpenHandsNET.Api.Endpoints;
using OpenHandsNET.Api.Routing;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Sandboxing;

using Xunit;

namespace OpenHandsNET.UnitTests.SandboxOps;

/// <summary>
/// SPEC-115 RF-001/RF-004: contrato de acesso às surfaces do sandbox.
/// Em <c>RoutingMode=internal</c> (default OSS) a URL entregue ao client é
/// SEMPRE o path relativo same-origin do proxy — o browser nunca fala
/// direto com o container (VNC, terminal, http todas via API).
/// </summary>
public sealed class Spec115SurfaceUrlTests
{
    private static Sandbox Sandbox() => new(
        Id: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        SpecId: Guid.NewGuid(),
        ConversationId: null,
        ContainerId: "abc123def456",
        Status: SandboxStatus.Running,
        AgentUrl: null,
        CreatedAt: DateTimeOffset.UtcNow,
        ExpiresAt: null,
        LastError: null);

    private static PreviewTokenService Tokens(SandboxOptions options) => new(
        Options.Create(options), NullLogger<PreviewTokenService>.Instance);

    [Theory]
    [InlineData(null)]
    [InlineData("internal")]
    [InlineData("INTERNAL")]
    public void InternalMode_ReturnsSameOriginProxyPath(string? routingMode)
    {
        var options = new SandboxOptions
        {
            RoutingMode = routingMode ?? "internal",
            // Mesmo com domínio público configurado, internal vence.
            PublicBaseDomain = "sandboxes.example.com",
            PreviewTokenSecret = "test-secret",
        };
        if (routingMode is null)
        {
            options.RoutingMode = "internal";
        }
        else
        {
            options.RoutingMode = routingMode;
        }

        var (url, expires) = AppConversationEndpoints.ResolveSurfaceUrl(
            options, Tokens(options), Sandbox(), new AgentSurface("vnc", 5900, "http"));

        Assert.Equal("/api/v1/sandboxes/11111111-1111-1111-1111-111111111111/surfaces/5900/", url);
        Assert.Null(expires);
        Assert.StartsWith("/", url);
        Assert.DoesNotContain("://", url);
    }

    [Fact]
    public void PathMode_ReturnsTokenizedUrl_NotBareHost()
    {
        var options = new SandboxOptions
        {
            RoutingMode = "path",
            PublicBaseDomain = "https://apps.example.com",
            PreviewTokenSecret = "test-secret",
        };

        var (url, expires) = AppConversationEndpoints.ResolveSurfaceUrl(
            options, Tokens(options), Sandbox(), new AgentSurface("vnc", 5900, "http"));

        Assert.NotNull(expires);
        Assert.Contains("token=", url);
        Assert.DoesNotContain("://abc123def456", url);
    }
}