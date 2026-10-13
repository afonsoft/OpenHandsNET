using System.Text.Json;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Domain.Mcp;

namespace OpenHandsNET.Application.Mcp;

/// <summary>Tool exposta por um servidor MCP conectado.</summary>
public sealed record McpToolDescriptor(
    string ServerName,
    string ToolName,
    string FullName,
    string? Description,
    JsonElement InputSchema);

/// <summary>Resultado de um probe <c>POST /mcp/test</c>.</summary>
public sealed record McpProbeResult(
    bool Ok, int ToolsCount, long LatencyMs, string? Error);

/// <summary>
/// Conexões MCP por conversa (SPEC-07 RF-002): connect/list/call; lifecycle
/// preso à conversa — falha de um servidor degrada o catálogo, nunca derruba.
/// </summary>
public interface IMcpClientService
{
    /// <summary>Catálogo completo (<c>mcp__{server}__{tool}</c>) da conversa.</summary>
    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(
        Guid conversationId, CancellationToken ct = default);

    /// <summary>Invoca uma tool; audita via ACP/Observation no EventStream.</summary>
    Task<ToolResult> CallToolAsync(
        Guid conversationId, string serverName, string toolName,
        JsonElement args, CancellationToken ct = default);

    /// <summary>Servidor exige confirmation (require_confirmation=true)?</summary>
    Task<bool> RequiresConfirmationAsync(
        Guid conversationId, string serverName, CancellationToken ct = default);

    /// <summary>Probe ad-hoc sem persistir (POST /mcp/test).</summary>
    Task<McpProbeResult> ProbeAsync(
        McpServerConfig config, CancellationToken ct = default);

    /// <summary>Fecha conexões órfãs da conversa.</summary>
    Task DisconnectAsync(Guid conversationId, CancellationToken ct = default);
}
