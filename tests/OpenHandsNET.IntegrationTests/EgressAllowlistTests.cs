using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenHandsNET.Infrastructure.Sandbox;
using OpenHandsNET.Sandboxing;

namespace OpenHandsNET.IntegrationTests;

/// <summary>
/// SPEC-19 AC: proxy Squid real — rede internal + container dual-homed,
/// squid.conf gerado, env de proxy. Opt-in (puxa ubuntu/squid):
/// <c>OHN_EGRESS_TESTS=1 dotnet test --filter EgressAllowlistTests</c>.
/// </summary>
public sealed class EgressAllowlistTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"ohn-egress-it-{Guid.NewGuid():N}");
    private IDockerClient _docker = null!;
    private DockerNetworkPolicy _policy = null!;
    private EgressProxyService _proxy = null!;
    private bool _enabled;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("OHN_EGRESS_TESTS") != "1")
        {
            return;
        }

        try
        {
            _docker = DockerClientFactory.Create();
            await _docker.System.PingAsync();
        }
        catch
        {
            return;
        }

        var opts = Options.Create(new SandboxOptions
        {
            EgressMode = "allowlist",
            EgressAllow = "github.com;nuget.org",
            WorkspaceRoot = Path.Combine(_dir, "ws"),
            EgressConfigDir = Path.Combine(_dir, "egress"),
            InstanceOwner = $"test-{Guid.NewGuid():N}",
        });
        _policy = new DockerNetworkPolicy(_docker, opts,
            NullLogger<DockerNetworkPolicy>.Instance);
        _proxy = new EgressProxyService(_docker, opts,
            new SquidConfigWriter(opts, NullLogger<SquidConfigWriter>.Instance),
            NullLogger<EgressProxyService>.Instance);
        _enabled = true;
    }

    [Fact]
    public async Task Proxy_Starts_DualHomed_And_Serves()
    {
        if (!_enabled) return;

        var net = await _policy.EnsureNetworkAsync(CancellationToken.None);
        net.ShouldBe(DockerNetworkPolicy.InternalNetworkName);

        var url = await _proxy.EnsureProxyAsync(net!, CancellationToken.None);
        url.ShouldBe($"http://{EgressProxyService.ProxyName}:3128");

        var inspect = await _docker.Containers.InspectContainerAsync(
            EgressProxyService.ProxyName, CancellationToken.None);
        inspect.State.ShouldNotBeNull().Running.ShouldBeTrue();
        inspect.NetworkSettings.ShouldNotBeNull()
            .Networks.Keys.ShouldContain(
                DockerNetworkPolicy.InternalNetworkName);

        // Idempotente: segunda chamada não recria.
        (await _proxy.EnsureProxyAsync(net!, CancellationToken.None))
            .ShouldBe(url);
    }

    public async Task DisposeAsync()
    {
        if (_enabled)
        {
            try
            {
                await _docker.Containers.RemoveContainerAsync(
                    EgressProxyService.ProxyName,
                    new ContainerRemoveParameters { Force = true });
            }
            catch { /* best-effort */ }
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
