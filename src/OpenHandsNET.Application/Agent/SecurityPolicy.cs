namespace OpenHandsNET.Application.Agent;

/// <summary>
/// SPEC-48 RF-001: effective org-scoped security policy — preset defaults
/// merged with explicit overrides. Resolved once per run and cached
/// (<see cref="ISecurityPolicyService"/>); a missing row yields the
/// <c>balanced</c> defaults (status quo, fail-open).
/// </summary>
/// <param name="Preset"><c>permissive</c> | <c>balanced</c> | <c>strict</c>.</param>
/// <param name="AnalyzerOverride"><c>pattern</c> | <c>llm</c> | <c>off</c> | null (inherit global).</param>
/// <param name="AskMcpTools">Force <c>ask</c> on <c>mcp__*</c> tools regardless of Risky.</param>
/// <param name="MinConfirmRisk">Analyzer risk floor that forces user confirmation.</param>
/// <param name="ResumeStrict">Fail-closed resume on toolset drift; null inherits <c>AgentLlm:StrictResume</c>.</param>
public sealed record SecurityPolicy(
    string Preset,
    string? AnalyzerOverride,
    bool AskMcpTools,
    SecurityRisk MinConfirmRisk,
    bool? ResumeStrict)
{
    /// <summary>Default policy when the org has no row (<c>balanced</c>).</summary>
    public static SecurityPolicy Balanced { get; } =
        SecurityPolicyPresets.Resolve("balanced", null);
}

/// <summary>
/// SPEC-48 RF-003: editable policy fields. Every field null = inherit the
/// preset default; a null <see cref="SecurityPolicyUpdate"/> resets to
/// <c>balanced</c> with no overrides.
/// </summary>
public sealed record SecurityPolicyUpdate(
    string? Preset,
    string? AnalyzerOverride,
    bool? AskMcpTools,
    string? MinConfirmRisk,
    bool? ResumeStrict);

/// <summary>Preset validation + effective-policy resolution (SPEC-48 RF-001).</summary>
public static class SecurityPolicyPresets
{
    /// <summary>Valid preset names.</summary>
    public static readonly IReadOnlySet<string> Names =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "permissive", "balanced", "strict" };

    /// <summary>Valid analyzer overrides (<c>null</c> = inherit global).</summary>
    public static readonly IReadOnlySet<string> Analyzers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "pattern", "llm", "off" };

    /// <summary>Valid confirmation floors.</summary>
    public static readonly IReadOnlySet<string> Risks =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "low", "medium", "high" };

    /// <summary>
    /// Merges explicit overrides over the preset defaults. Preset table:
    /// <c>permissive</c> — global analyzer, High floor, no mcp ask;
    /// <c>balanced</c> — same gates (analyzer inherits the global default,
    /// which is pattern); <c>strict</c> — prefers the LLM analyzer (runner
    /// falls back to pattern + diagnostic when unconfigured), Medium floor,
    /// mcp ask, strict resume.
    /// </summary>
    public static SecurityPolicy Resolve(
        string? preset, SecurityPolicyUpdate? overrides)
    {
        var p = (preset ?? "balanced").ToLowerInvariant();
        var basePolicy = p switch
        {
            "permissive" => new SecurityPolicy(
                p, null, AskMcpTools: false,
                MinConfirmRisk: SecurityRisk.High, ResumeStrict: null),
            "strict" => new SecurityPolicy(
                p, "llm", AskMcpTools: true,
                MinConfirmRisk: SecurityRisk.Medium, ResumeStrict: true),
            _ => new SecurityPolicy(
                "balanced", null, AskMcpTools: false,
                MinConfirmRisk: SecurityRisk.High, ResumeStrict: null),
        };
        if (overrides is null)
        {
            return basePolicy;
        }

        return basePolicy with
        {
            AnalyzerOverride = overrides.AnalyzerOverride ?? basePolicy.AnalyzerOverride,
            AskMcpTools = overrides.AskMcpTools ?? basePolicy.AskMcpTools,
            MinConfirmRisk = ParseRisk(overrides.MinConfirmRisk)
                ?? basePolicy.MinConfirmRisk,
            ResumeStrict = overrides.ResumeStrict ?? basePolicy.ResumeStrict,
        };
    }

    /// <summary>Parses a risk floor; null/empty → null (inherit).</summary>
    public static SecurityRisk? ParseRisk(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "low" => SecurityRisk.Low,
            "medium" => SecurityRisk.Medium,
            "high" => SecurityRisk.High,
            _ => null,
        };
}

/// <summary>
/// SPEC-48: org security policy lookup (cached, TTL 60s) + admin writes.
/// </summary>
public interface ISecurityPolicyService
{
    /// <summary>Effective policy for an org (defaults when none/null).</summary>
    Task<SecurityPolicy> GetAsync(Guid? orgId, CancellationToken ct);

    /// <summary>
    /// Upserts the org policy (<paramref name="update"/> null resets to
    /// <c>balanced</c>) and invalidates the cache. Returns the effective policy.
    /// </summary>
    Task<SecurityPolicy> SetAsync(
        Guid orgId, SecurityPolicyUpdate? update, Guid? actorUserId,
        CancellationToken ct);
}
