using System.Text.Json;

namespace Sandcastle.Application.Agent;

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
    /// <summary>
    /// Analyzes one tool call; must be fast (pattern) or cached (llm).
    /// <paramref name="routeCtx"/> carries the run's org/user so LLM-backed
    /// analyzers resolve the <c>critic</c> role under the org routing rules
    /// (SPEC-149); null = org-less (global candidates only).
    /// </summary>
    Task<SecurityRisk> AnalyzeAsync(string toolName,
        JsonElement action, CancellationToken ct,
        LlmRouteContext? routeCtx = null);
}