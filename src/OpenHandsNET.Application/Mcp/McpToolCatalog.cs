using System.Text.Json;

using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Application.Mcp;

/// <summary>
/// Naming bridge MCP ↔ agente (SPEC-07 RF-004):
/// <c>mcp__{server}__{tool}</c>, descrição truncada a 1024 chars.
/// </summary>
public static class McpToolCatalog
{
    /// <summary>Prefixo que separa tools MCP dos builtins.</summary>
    public const string Prefix = "mcp__";

    /// <summary>Limite de descrição (paridade regra upstream).</summary>
    public const int MaxDescriptionLength = 1024;

    /// <summary>Full name com prefixo.</summary>
    public static string FullName(string server, string tool) =>
        $"{Prefix}{server}__{tool}";

    /// <summary>
    /// Resolve <c>mcp__{server}__{tool}</c> → (server, tool);
    /// <see langword="false"/> se não for nome MCP válido.
    /// </summary>
    public static bool TryParse(
        string fullName, out string server, out string tool)
    {
        server = tool = "";
        if (!fullName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = fullName[Prefix.Length..];
        var sep = rest.IndexOf("__", StringComparison.Ordinal);
        if (sep <= 0 || sep + 2 >= rest.Length)
        {
            return false;
        }

        server = rest[..sep];
        tool = rest[(sep + 2)..];
        return !server.Contains("__", StringComparison.Ordinal);
    }

    /// <summary>Schema de tool para o LLM (nome prefixado + descrição).</summary>
    public static ToolSchema ToSchema(McpToolDescriptor d) =>
        new(FullName(d.ServerName, d.ToolName),
            Truncate(d.Description), d.InputSchema);

    private static string Truncate(string? s) =>
        s is null ? "" :
        s.Length <= MaxDescriptionLength ? s : s[..MaxDescriptionLength];
}