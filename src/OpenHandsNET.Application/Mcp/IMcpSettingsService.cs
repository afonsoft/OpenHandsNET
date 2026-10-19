using OpenHandsNET.Domain.Mcp;

namespace OpenHandsNET.Application.Mcp;

/// <summary>CRUD do flat map de servidores MCP por owner (SPEC-07 RF-001).</summary>
public interface IMcpSettingsService
{
    /// <summary>Todos os servidores do owner.</summary>
    Task<IReadOnlyList<McpServerConfig>> ListAsync(
        string ownerId, CancellationToken ct = default);

    /// <summary>Busca por nome (<see langword="null"/> se ausente).</summary>
    Task<McpServerConfig?> GetAsync(
        string ownerId, string name, CancellationToken ct = default);

    /// <summary>Upsert por nome.</summary>
    Task SaveAsync(
        string ownerId, McpServerConfig config, CancellationToken ct = default);

    /// <summary>Remove; devolve se existia.</summary>
    Task<bool> DeleteAsync(
        string ownerId, string name, CancellationToken ct = default);

    /// <summary>
    /// Config efetiva de uma conversa: override JSON da conversa se presente,
    /// senão o mapa global do owner.
    /// </summary>
    Task<IReadOnlyList<McpServerConfig>> EffectiveForAsync(
        string ownerId, string? conversationOverrideJson,
        CancellationToken ct = default);
}