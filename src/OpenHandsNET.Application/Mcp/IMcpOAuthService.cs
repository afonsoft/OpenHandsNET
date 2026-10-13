using OpenHandsNET.Domain.Mcp;

namespace OpenHandsNET.Application.Mcp;

/// <summary>
/// OAuth 2.1 + PKCE para servidores MCP remotos (SPEC-07 RF-003).
/// Tokens cifrados no secrets store; refresh automático antes de expirar.
/// </summary>
public interface IMcpOAuthService
{
    /// <summary>
    /// Monta o authorization URL (state+PKCE persistidos) para redirect.
    /// Devolve <see langword="null"/> se o servidor não tiver OAuth configurado.
    /// </summary>
    Task<Uri?> BuildAuthorizeUrlAsync(
        string ownerId, string serverName, Uri callbackUri,
        CancellationToken ct = default);

    /// <summary>
    /// Troca <c>code</c>+<c>state</c> por tokens e persiste cifrados.
    /// Devolve o nome do servidor autenticado.
    /// </summary>
    Task<string> HandleCallbackAsync(
        string code, string state, Uri callbackUri,
        CancellationToken ct = default);

    /// <summary>Access token válido (refresh se perto de expirar).</summary>
    Task<string?> GetAccessTokenAsync(
        string ownerId, string serverName, CancellationToken ct = default);

    /// <summary>Revoga e remove os tokens do servidor.</summary>
    Task DisconnectAsync(
        string ownerId, string serverName, CancellationToken ct = default);

    /// <summary>Se o servidor já tem token válido armazenado.</summary>
    Task<bool> IsAuthenticatedAsync(
        string ownerId, string serverName, CancellationToken ct = default);
}
