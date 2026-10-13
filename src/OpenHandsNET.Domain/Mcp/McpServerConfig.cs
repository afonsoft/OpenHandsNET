using System.Text.Json.Serialization;

namespace OpenHandsNET.Domain.Mcp;

/// <summary>Transportes MCP suportados (SPEC-07 RF-001).</summary>
public enum McpTransport
{
    /// <summary>Processo filho via stdin/stdout (docker exec ou host c/ flag).</summary>
    Stdio,

    /// <summary>Server-Sent Events legado (HTTP).</summary>
    Sse,

    /// <summary>Streamable HTTP (spec atual).</summary>
    Http,
}

/// <summary>
/// Flat-map config de um servidor MCP (paridade SDK upstream — sem wrapper
/// <c>.mcp.json</c>). Serializado como JSON em <c>mcp_servers.config_json</c>.
/// </summary>
public sealed record McpServerConfig
{
    /// <summary>Nome único do servidor (chave do flat map).</summary>
    public required string Name { get; init; }

    /// <summary>Transporte de conexão.</summary>
    public McpTransport Transport { get; init; } = McpTransport.Stdio;

    /// <summary>Comando (stdio) — executado via <c>docker exec</c> no sandbox.</summary>
    public string? Command { get; init; }

    /// <summary>Argumentos do comando.</summary>
    public IReadOnlyList<string>? Args { get; init; }

    /// <summary>Endpoint (sse/http).</summary>
    public Uri? Url { get; init; }

    /// <summary>Env vars do processo stdio.</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>Headers HTTP (sse/http) — Authorization vem do OAuth store.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Servidor ativo no catálogo.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Calls exigem aprovação no confirmation_mode (SPEC-09).</summary>
    public bool RequireConfirmation { get; init; }

    /// <summary>Timeout por tool call (default 60s na guardrail da SPEC).</summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Endpoint OAuth authorization (servidores remotos).</summary>
    [JsonPropertyName("oauth_authorize_url")]
    public Uri? OAuthAuthorizeUrl { get; init; }

    /// <summary>Endpoint OAuth token.</summary>
    [JsonPropertyName("oauth_token_url")]
    public Uri? OAuthTokenUrl { get; init; }

    /// <summary>Client id OAuth.</summary>
    [JsonPropertyName("oauth_client_id")]
    public string? OAuthClientId { get; init; }
}
