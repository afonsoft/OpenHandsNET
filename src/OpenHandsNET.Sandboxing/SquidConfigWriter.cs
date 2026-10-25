using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenHandsNET.Sandboxing;

/// <summary>
/// SPEC-19 RF-002: materializes <c>squid.conf</c> + <c>allowlist.txt</c> for
/// the shared egress proxy under <c>{Sandbox:EgressConfigDir}</c> (default
/// <c>{WorkspaceRoot}/../egress</c>). Returns a content hash — the caller
/// recreates the proxy container when it changes.
/// </summary>
public sealed class SquidConfigWriter(
    IOptions<SandboxOptions> options,
    ILogger<SquidConfigWriter> logger)
{
    /// <summary>Generated config file names.</summary>
    public const string ConfFile = "squid.conf";
    public const string AllowFile = "allowlist.txt";

    /// <summary>Resolves the host config dir (created on demand).</summary>
    public string ConfigDir
    {
        get
        {
            var dir = options.Value.EgressConfigDir
                ?? Path.Combine(
                    Path.GetFullPath(options.Value.WorkspaceRoot), "..", "egress");
            return Path.GetFullPath(dir);
        }
    }

    /// <summary>
    /// Writes the config pair and returns a hash of the rendered content.
    /// Idempotent — rewrites only when content differs.
    /// </summary>
    public string Write()
    {
        var dir = ConfigDir;
        Directory.CreateDirectory(dir);
        var domains = NormalizeDomains(options.Value.EgressAllow);
        var conf = SquidConf;
        var allow = RenderAllowlist(domains);
        WriteIfChanged(Path.Combine(dir, ConfFile), conf);
        WriteIfChanged(Path.Combine(dir, AllowFile), allow);
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(conf + "\n" + allow)));
        logger.LogDebug("egress proxy config written to {Dir} (hash {Hash})",
            dir, hash[..12]);
        return hash;
    }

    /// <summary>squid.conf — fail-closed: nada passa sem dstdomain match.</summary>
    internal const string SquidConf = """
        # OpenHandsNET egress allowlist proxy (SPEC-19) — gerado, não editar.
        http_port 3128
        acl allowed dstdomain "/etc/squid/allowlist.txt"
        acl CONNECT method CONNECT
        acl SSL_ports port 443
        acl Safe_ports port 80 443
        http_access deny !Safe_ports
        http_access deny CONNECT !SSL_ports
        http_access allow allowed
        http_access deny all
        dns_v4_first on
        access_log none
        cache deny all
        coredump_dir /var/spool/squid
        """;

    /// <summary>One normalized domain per line (<c>.dom.tld</c> = domínio+subs).</summary>
    internal static string RenderAllowlist(IReadOnlyList<string> domains) =>
        domains.Count == 0
            ? "# empty allowlist — proxy denies everything\n"
            : string.Join('\n', domains) + "\n";

    /// <summary>
    /// Normalizes <c>EgressAllow</c> (separadores <c>;,</c> ou whitespace):
    /// strip scheme/path/port, lowercase, prefixo <c>.</c> estilo squid para
    /// cobrir subdomínios. Entradas inválidas são descartadas.
    /// </summary>
    internal static List<string> NormalizeDomains(string? allow)
    {
        if (string.IsNullOrWhiteSpace(allow))
        {
            return [];
        }

        var domains = new List<string>();
        foreach (var raw in allow.Split(
                     [';', ',', ' ', '\n', '\t'],
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            var d = raw.TrimStart('*').TrimStart('.').ToLowerInvariant();
            if (d.Contains("://", StringComparison.Ordinal))
            {
                d = d[(d.IndexOf("://", StringComparison.Ordinal) + 3)..];
            }

            d = d.Split(['/', ':'], 2)[0];
            if (d.Length == 0 || d.Any(char.IsWhiteSpace))
            {
                continue;
            }

            domains.Add('.' + d);
        }

        return domains.Distinct().Order().ToList();
    }

    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return;
        }

        File.WriteAllText(path, content);
    }
}