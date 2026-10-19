namespace OpenHandsNET.Infrastructure.Mcp;

/// <summary>
/// <c>Mcp:*</c> — guardrails de execução MCP (SPEC-07 §10).
/// </summary>
public sealed class McpOptions
{
    /// <summary>Config section.</summary>
    public const string SectionName = "Mcp";

    /// <summary>
    /// Permite spawn stdio **no host** — dev only, nunca ligado por default.
    /// Sem a flag, stdio só roda dentro do sandbox via docker exec.
    /// </summary>
    public bool AllowHostMcp { get; set; }

    /// <summary>Timeout default de tool call quando a config não define.</summary>
    public int DefaultCallTimeoutSeconds { get; set; } = 60;

    /// <summary>Janela de antecedência para refresh do token OAuth.</summary>
    public int OAuthRefreshSkewSeconds { get; set; } = 60;
}