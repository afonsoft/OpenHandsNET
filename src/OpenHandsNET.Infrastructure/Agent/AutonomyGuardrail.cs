using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenHandsNET.Infrastructure.Agent;

/// <summary>
/// SPEC-15 RF-003 guardrail: even in always-allow mode
/// (<c>confirmation_mode=false</c>) a destructive action still requires a
/// human answer — host-boundary tools (<c>mcp__*</c>), writes outside
/// <c>/workspace</c>, and shell commands with destructive patterns
/// (force-push, recursive delete outside the workspace, device/system
/// killers). Deterministic: pattern matching only, no LLM judgement.
/// </summary>
public static class AutonomyGuardrail
{
    private const string WorkspaceRoot = "/workspace";

    /// <summary>True when the action must pause for human approval even in always-allow mode.</summary>
    /// <param name="toolName">Resolved tool name (<c>bash</c>, <c>file_write</c>, <c>mcp__…</c>…).</param>
    /// <param name="args">Parsed tool arguments (may be null).</param>
    public static bool RequiresHumanApproval(string toolName, JsonElement? args)
    {
        // Host-attached MCP — fora do sandbox, fronteira de credenciais.
        if (toolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return true;
        }

        return toolName switch
        {
            "bash" => IsDestructiveCommand(
                args?.TryGetProperty("command", out var c) == true
                    ? c.GetString() ?? string.Empty
                    : string.Empty),
            "file_write" or "file_edit" =>
                args?.TryGetProperty("path", out var p) == true &&
                IsOutsideWorkspace(p.GetString() ?? string.Empty),
            _ => false,
        };
    }

    private static bool IsOutsideWorkspace(string path)
    {
        var p = path.Trim();
        if (p.Length == 0)
        {
            return false;
        }

        if (p.StartsWith("~", StringComparison.Ordinal) ||
            p.StartsWith("..", StringComparison.Ordinal) ||
            p.Contains("/..", StringComparison.Ordinal))
        {
            return true;
        }

        // Absoluto fora de /workspace ("/etc/x", "/", "/tmp/x").
        return p.StartsWith('/') &&
               !p.Equals(WorkspaceRoot, StringComparison.Ordinal) &&
               !p.StartsWith(WorkspaceRoot + '/', StringComparison.Ordinal);
    }

    private static readonly Regex ForcePush = new(
        @"\bgit\s+push\b[^|&;]*(--force\b|--force-with-lease\b|-[a-zA-Z]*f[a-zA-Z]*(\s|$))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private static readonly Regex SystemKillers = new(
        @"\b(mkfs[a-z0-9.]*|shutdown|reboot|poweroff|halt|fdisk|init\s+[06])\b|" +
        @"\bdd\b[^|&;]*\bof=/dev/|:\(\)\s*\{",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private static bool IsDestructiveCommand(string command)
    {
        if (command.Length == 0)
        {
            return false;
        }

        if (ForcePush.IsMatch(command) || SystemKillers.IsMatch(command))
        {
            return true;
        }

        // rm/chmod/chown em alvo fora do workspace — análise por segmento.
        return command.Split('|', '&', ';').Any(IsDangerousSegment);
    }

    private static bool IsDangerousSegment(string seg)
    {
        var tokens = seg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return false;
        }

        var cmd = Path.GetFileName(tokens[0]);
        var isRm = cmd == "rm";
        var isPerm = cmd is "chmod" or "chown";
        if (!isRm && !isPerm)
        {
            return false;
        }

        var recursive = tokens.Skip(1).Any(t =>
            t.StartsWith('-') && t.Contains('R', StringComparison.OrdinalIgnoreCase) ||
            t == "--recursive");
        var force = isRm && tokens.Skip(1).Any(t =>
            t.StartsWith('-') && !t.StartsWith("--") &&
            t.Contains('f', StringComparison.OrdinalIgnoreCase) ||
            t == "--force");
        if (!recursive && !force)
        {
            return false;
        }

        return tokens.Skip(1)
            .Where(x => !x.StartsWith('-'))
            .Any(t => IsOutsideWorkspace(t) || t is "/" or "~" or "/*" or "$HOME");
    }
}