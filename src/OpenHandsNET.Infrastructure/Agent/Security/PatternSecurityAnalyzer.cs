using System.Text.Json;
using System.Text.RegularExpressions;
using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.Security;

/// <summary>
/// SPEC-33 RF-001: local pattern analyzer (default) — destructive shells,
/// filesystem escapes, supply-chain sinks. O(args), deterministic, no I/O.
/// </summary>
public sealed partial class PatternSecurityAnalyzer : ISecurityAnalyzer
{
    // Qualquer hit ⇒ High (forced confirmation) — cobre rm recursivo em
    // caminhos de sistema/home, dd em block devices, fork bomb, mkfs/fdisk,
    // pipe-to-shell (curl|sh), chmod 777 recursivo na raiz, push --force na
    // branch principal e --no-preserve-root.
    private static readonly Regex[] HighRules =
    [
        RmRecursiveSystemRx(), RmNoPreserveRx(), BlockDeviceRx(),
        ForkBombRx(), MkfsRx(), PipeToShellRx(), ChmodRx(), ForcePushRx(),
        HostDockerSocketEscapeRx(),
    ];

    // Comandos de risco médio — anotados na observation.
    private static readonly Regex[] MediumRules =
    [
        SudoRx(), DownloadBinaryRx(), KillRx(), CrontabRx(),
    ];

    /// <inheritdoc />
    public Task<SecurityRisk> AnalyzeAsync(
        string toolName, JsonElement action, CancellationToken ct)
    {
        var payload = ExtractPayload(toolName, action);
        if (payload is null)
        {
            return Task.FromResult(SecurityRisk.Low);
        }

        foreach (var rx in HighRules)
        {
            if (rx.IsMatch(payload))
            {
                return Task.FromResult(SecurityRisk.High);
            }
        }

        foreach (var rx in MediumRules)
        {
            if (rx.IsMatch(payload))
            {
                return Task.FromResult(SecurityRisk.Medium);
            }
        }

        return Task.FromResult(SecurityRisk.Low);
    }

    /// <summary>Extrai o "payload executável" por tool (bash/file/mcp).</summary>
    private static string? ExtractPayload(
        string toolName, JsonElement action)
    {
        if (action.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (toolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            // Args completos — a policy cobre padrões genéricos (paths, rm…).
            return action.GetRawText();
        }

        var field = toolName switch
        {
            "file_write" => "content",
            "git_commit" => "message",
            _ => "command",
        };
        return action.TryGetProperty(field, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
    }

    // rm recursivo (-r/-R em qualquer posição do cluster) mirando raiz,
    // home ou dirs de sistema — inclui `rm -rf /`, `rm -fr ~`, `rm -r /etc`.
    [GeneratedRegex(@"\brm\b\s+[^;|&]*-\w*[rR]\w*\s+[^;|&]*(/\s*$|/\s|/\*|~|/(home|usr|etc|var|opt|bin|sbin|lib|root|boot|proc|sys|dev|workspace|srv|mnt)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RmRecursiveSystemRx();

    [GeneratedRegex(@"\brm\b[^;|&]*--no-preserve-root",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RmNoPreserveRx();

    [GeneratedRegex(@"\bdd\b[^;|&]*\bof=/dev/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockDeviceRx();

    [GeneratedRegex(@":\s*\(\s*\)\s*\{\s*:\s*\|\s*:\s*&\s*\}\s*;",
        RegexOptions.CultureInvariant)]
    private static partial Regex ForkBombRx();

    [GeneratedRegex(@"\bmkfs\b|\bfdisk\b|\bshred\b[^;|&]*/dev/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MkfsRx();

    [GeneratedRegex(@"(curl|wget)\b[^|]*\|\s*(sudo\s+)?(ba)?sh\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PipeToShellRx();

    [GeneratedRegex(@"\b(docker|podman)\b.*-v\s+.*(/var/run/docker\.sock|/run/user/\d+/docker\.sock|/run/podman/podman\.sock)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostDockerSocketEscapeRx();

    [GeneratedRegex(@"\bchmod\b\s+-\w*[rR]\w*\s+7[67]7\s+(/|~)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChmodRx();

    [GeneratedRegex(@"\bgit\s+push\b[^;|&]*(--force|-f)[^;|&]*\b(main|master)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForcePushRx();

    [GeneratedRegex(@"\bsudo\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SudoRx();

    [GeneratedRegex(@"(curl|wget)\b[^;|]*>\s*/tmp/\S+\s*&&\s*(chmod|sh|bash|\./)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DownloadBinaryRx();

    [GeneratedRegex(@"\b(kill(all)?|pkill)\b[^;|&]*-9",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KillRx();

    [GeneratedRegex(@"\bcrontab\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CrontabRx();
}
