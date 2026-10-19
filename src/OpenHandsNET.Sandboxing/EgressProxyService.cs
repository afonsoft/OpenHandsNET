using Docker.DotNet;
using Docker.DotNet.Models;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenHandsNET.Sandboxing;

/// <summary>
/// SPEC-19 RF-001/003/004: shared Squid forward-proxy attached to the
/// internal egress network + default bridge (dual-homed). Sandboxes in
/// <c>allowlist</c> mode reach the internet only through it — the
/// <c>dstdomain</c> ACL in <c>allowlist.txt</c> does per-domain enforcement.
/// <para>
/// Fail-closed contract: <see cref="EnsureProxyAsync"/> throws on any
/// failure — <c>StartAsync</c> must never degrade allowlist to open egress.
/// </para>
/// </summary>
public sealed class EgressProxyService(
    IDockerClient docker,
    IOptions<SandboxOptions> options,
    SquidConfigWriter configWriter,
    ILogger<EgressProxyService> logger)
{
    /// <summary>Container name resolvable via embedded DNS on the internal net.</summary>
    public const string ProxyName = "openhandsnet-egress-proxy";

    /// <summary>Squid listen port.</summary>
    public const int ProxyPort = 3128;

    private static string ConfigHashLabel =>
        $"{EngineLabels.Prefix}config_hash";

    /// <summary>Proxy URL for sandbox env (container name resolves on the net).</summary>
    public static string ProxyUrl => $"http://{ProxyName}:{ProxyPort}";

    /// <summary>proxy env vars injected into allowlist-mode sandboxes.</summary>
    public static IReadOnlyList<string> ProxyEnv() =>
    [
        $"HTTP_PROXY={ProxyUrl}", $"HTTPS_PROXY={ProxyUrl}",
        $"http_proxy={ProxyUrl}", $"https_proxy={ProxyUrl}",
        "NO_PROXY=localhost,127.0.0.1,::1",
        "no_proxy=localhost,127.0.0.1,::1",
    ];

    /// <summary>
    /// Ensures the proxy is running with the current allowlist config.
    /// Recreates the container when the rendered config hash changed.
    /// Returns the proxy URL reachable from the isolated network.
    /// </summary>
    public async Task<string> EnsureProxyAsync(
        string internalNetwork, CancellationToken ct)
    {
        var hash = configWriter.Write();
        var existing = await FindAsync(ct);
        if (existing is not null)
        {
            var hashMatches = existing.Labels is not null
                && existing.Labels.TryGetValue(ConfigHashLabel, out var h)
                && h == hash;
            if (!hashMatches)
            {
                // Conf mudou → recria (conf de bind é imutável no container).
                logger.LogInformation(
                    "egress proxy config changed — recreating {Name}", ProxyName);
                await RemoveAsync(existing.ID, ct);
            }
            else if (existing.State == "running")
            {
                return ProxyUrl;
            }
            else
            {
                await docker.Containers.StartContainerAsync(
                    existing.ID, null, ct);
                logger.LogInformation("egress proxy {Name} restarted", ProxyName);
                return ProxyUrl;
            }
        }

        await EnsureImageAsync(ct);
        var created = await docker.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = options.Value.EgressProxyImage,
                Name = ProxyName,
                Labels = new Dictionary<string, string>
                {
                    [$"{EngineLabels.Prefix}managed"] = "true",
                    [$"{EngineLabels.Prefix}owner"] =
                        options.Value.InstanceOwner,
                    [$"{EngineLabels.Prefix}role"] = "egress-proxy",
                    [ConfigHashLabel] = hash,
                },
                HostConfig = new HostConfig
                {
                    RestartPolicy = new RestartPolicy
                    {
                        Name = RestartPolicyKind.UnlessStopped,
                    },
                    Binds =
                    [
                        $"{configWriter.ConfigDir}:/etc/squid:ro",
                    ],
                },
            }, ct);

        // Dual-homed: nasce na bridge default (saída real) e conecta na rede
        // internal onde os sandboxes o alcançam pelo nome.
        await docker.Networks.ConnectNetworkAsync(internalNetwork,
            new NetworkConnectParameters { Container = created.ID }, ct);
        await docker.Containers.StartContainerAsync(created.ID, null, ct);
        logger.LogInformation(
            "egress proxy {Name} running (container {Id}, hash {Hash})",
            ProxyName, created.ID[..12], hash[..12]);
        return ProxyUrl;
    }

    /// <summary>Finds the proxy container (any state), or null.</summary>
    private async Task<ContainerListResponse?> FindAsync(CancellationToken ct)
    {
        var containers = await docker.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["name"] = new Dictionary<string, bool>
                    {
                        [ProxyName] = true,
                    },
                    ["label"] = new Dictionary<string, bool>
                    {
                        [$"{EngineLabels.Prefix}managed"] = true,
                    },
                },
            }, ct);
        return containers.FirstOrDefault(c =>
            c.Names.Any(n => n.TrimStart('/') == ProxyName));
    }

    private async Task RemoveAsync(string id, CancellationToken ct) =>
        await docker.Containers.RemoveContainerAsync(id,
            new ContainerRemoveParameters { Force = true }, ct);

    private async Task EnsureImageAsync(CancellationToken ct)
    {
        var image = options.Value.EgressProxyImage;
        var images = await docker.Images.ListImagesAsync(
            new ImagesListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["reference"] = new Dictionary<string, bool>
                    {
                        [image] = true,
                    },
                },
            }, ct);
        if (images.Count > 0)
        {
            return;
        }

        logger.LogInformation("pulling egress proxy image {Image}", image);
        var (name, tag) = image.Split(':', 2) is { Length: 2 } p
            ? (p[0], p[1]) : (image, "latest");
        await docker.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = name, Tag = tag },
            null, new Progress<JSONMessage>(), ct);
    }
}