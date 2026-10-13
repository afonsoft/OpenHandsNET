using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenHandsNET.Infrastructure.Sandbox;

/// <summary>
/// Egress policy wiring (SPEC-14 RF-002): <c>deny</c> and <c>allowlist</c>
/// place sandboxes on an internal-only docker network (no outbound route);
/// <c>unrestricted</c> keeps the default bridge. Fine-grained allowlist
/// filtering (per-host) is documented as a proxy-sidecar limitation — the
/// allowlist value is propagated to the sandbox env for future enforcement.
/// </summary>
public sealed class DockerNetworkPolicy(
    IDockerClient docker,
    IOptions<SandboxOptions> options,
    ILogger<DockerNetworkPolicy> logger)
{
    /// <summary>Internal network name for restricted egress.</summary>
    public const string InternalNetworkName = "openhandsnet-egress-isolated";

    /// <summary>True when sandbox traffic should be network-isolated.</summary>
    public bool IsRestricted =>
        !options.Value.EgressMode.Equals("unrestricted", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ensures the internal network exists (internal:true → no outbound).
    /// Returns the network name to attach, or null for default bridge.
    /// </summary>
    public async Task<string?> EnsureNetworkAsync(CancellationToken ct)
    {
        if (!IsRestricted)
        {
            return null;
        }

        var nets = await docker.Networks.ListNetworksAsync(
            new NetworksListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["name"] = new Dictionary<string, bool>
                    {
                        [InternalNetworkName] = true,
                    },
                },
            }, ct);
        var existing = nets.FirstOrDefault(n => n.Name == InternalNetworkName);
        if (existing is not null)
        {
            return existing.Name;
        }

        var created = await docker.Networks.CreateNetworkAsync(
            new NetworksCreateParameters
            {
                Name = InternalNetworkName,
                Driver = "bridge",
                Internal = true,
                Labels = new Dictionary<string, string>
                {
                    [$"{DockerSandboxService.LabelPrefix}managed"] = "true",
                    [$"{DockerSandboxService.LabelPrefix}owner"] =
                        options.Value.InstanceOwner,
                },
            }, ct);
        logger.LogInformation(
            "Created internal egress network {Network} ({Id})",
            InternalNetworkName, created.ID[..12]);
        return InternalNetworkName;
    }

    /// <summary>Extra env vars the sandbox receives for egress transparency.</summary>
    public IReadOnlyList<string> EgressEnv()
    {
        if (!IsRestricted)
        {
            return [];
        }

        var mode = options.Value.EgressMode.ToLowerInvariant();
        var vars = new List<string> { $"EGRESS_MODE={mode}" };
        if (mode == "allowlist" && options.Value.EgressAllow is { } allow)
        {
            vars.Add($"EGRESS_ALLOWLIST={allow}");
        }
        return vars;
    }
}
