using OpenHandsNET.Domain.Sandbox;

namespace OpenHandsNET.Sandboxing;

/// <summary>
/// SPEC-38 RF-003: emite <c>service_urls</c> conforme <c>RoutingMode</c>:
/// internal → omitido (não user-facing); path → <c>/sandbox/{id}/port/{p}</c>;
/// host → <c>https://{id}-{p}.{PublicBaseDomain}</c>.
/// </summary>
public static class SandboxUrlBuilder
{
    /// <summary>Portas publicáveis = agente (8000 quando há AgentUrl) + PublicPorts.</summary>
    public static IReadOnlyDictionary<int, string>? Build(
        Sandbox sandbox, SandboxSpec? spec, SandboxOptions options)
    {
        if ((options.RoutingMode ?? "internal").Equals(
                "internal", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var ports = new SortedSet<int>(
            spec?.PublicPorts ?? (IReadOnlyList<int>)[]);
        if (sandbox.AgentUrl is not null)
        {
            ports.Add(8000);
        }

        if (ports.Count == 0)
        {
            return null;
        }

        var urls = new Dictionary<int, string>();
        foreach (var p in ports)
        {
            urls[p] = BuildFor(options, sandbox.Id, p);
        }

        return urls;
    }

    /// <summary>URL de uma porta num modo não-internal.</summary>
    public static string BuildFor(SandboxOptions options, Guid id, int port) =>
        (options.RoutingMode ?? "internal").ToLowerInvariant() switch
        {
            "path" => $"/sandbox/{id:N}/port/{port}",
            "host" when options.PublicBaseDomain is { } d =>
                $"https://{id:N}-{port}.{d}",
            _ => $"/sandbox/{id:N}/port/{port}",
        };
}
