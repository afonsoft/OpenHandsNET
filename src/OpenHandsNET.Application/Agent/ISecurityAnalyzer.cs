using System.Text.Json;

namespace OpenHandsNET.Application.Agent;

/// <summary>Action risk level (SPEC-33 RF-001).</summary>
public enum SecurityRisk { Low, Medium, High }

/// <summary>
/// Per-tool-call security risk evaluation (SPEC-33 RF-001 — upstream
/// <c>SecurityAnalyzer</c> concept). Runs <em>before</em> the permission
/// gate: <see cref="SecurityRisk.High"/> forces user confirmation,
/// <see cref="SecurityRisk.Medium"/> is annotated on the observation.
/// </summary>
public interface ISecurityAnalyzer
{
    /// <summary>Analyzes one tool call; must be fast (pattern) or cached (llm).</summary>
    Task<SecurityRisk> AnalyzeAsync(string toolName,
        JsonElement action, CancellationToken ct);
}