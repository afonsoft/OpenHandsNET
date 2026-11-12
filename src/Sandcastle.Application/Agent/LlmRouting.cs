using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sandcastle.Application.Agent;

/// <summary>
/// SPEC-149 RF-001: canonical LLM call role — the single vocabulary the
/// model router resolves for. Wire names are lowercase
/// (<see cref="LlmRoles.ToWire"/>); <see cref="ModelRole"/> callers map via
/// <see cref="LlmRoles.FromModelRole"/> (kept for source compat).
/// </summary>
public enum LlmRole
{
    /// <summary>Planning passes (SPEC-23 planner, refactor plans).</summary>
    Plan,

    /// <summary>Main agent loop (ReAct worker) — user model pin applies.</summary>
    Code,

    /// <summary>PR review / QA plan pipelines (SPECs 140/142).</summary>
    Review,

    /// <summary>Analysis passes (scan dedup SPEC-143, misc analysis).</summary>
    Analysis,

    /// <summary>Interactive chat surfaces — user model pin applies.</summary>
    Chat,

    /// <summary>Embedding calls (reserved — no provider today).</summary>
    Embedding,

    /// <summary>Second-opinion/judgement calls (advisor SPEC-125, LLM security analyzers SPEC-33/123).</summary>
    Critic,

    /// <summary>Compaction/wiki summaries (SPEC-22/145 summarizer).</summary>
    Summarizer,

    /// <summary>Subagent loops (SPEC-28).</summary>
    Subagent,
}

/// <summary>Wire helpers + <see cref="ModelRole"/> bridging (SPEC-149).</summary>
public static class LlmRoles
{
    /// <summary>Lowercase wire name (e.g. <c>code</c>).</summary>
    public static string ToWire(LlmRole role) =>
        role.ToString().ToLowerInvariant();

    /// <summary>Parses a wire name (null when unknown).</summary>
    public static LlmRole? FromWire(string? wire) => wire?.Trim()
        .ToLowerInvariant() switch
    {
        "plan" => LlmRole.Plan,
        "code" => LlmRole.Code,
        "review" => LlmRole.Review,
        "analysis" => LlmRole.Analysis,
        "chat" => LlmRole.Chat,
        "embedding" => LlmRole.Embedding,
        "critic" => LlmRole.Critic,
        "summarizer" => LlmRole.Summarizer,
        "subagent" => LlmRole.Subagent,
        // Aliases do legado SPEC-23 — documentos antigos escritos com os
        // nomes do ModelRole continuam resolvendo para a role certa.
        "worker" => LlmRole.Code,
        "reviewer" => LlmRole.Review,
        "planner" => LlmRole.Plan,
        _ => null,
    };

    /// <summary>Maps the legacy <see cref="ModelRole"/> onto the canonical role.</summary>
    public static LlmRole FromModelRole(ModelRole role) => role switch
    {
        ModelRole.Worker => LlmRole.Code,
        ModelRole.Subagent => LlmRole.Subagent,
        ModelRole.Planner => LlmRole.Plan,
        ModelRole.Reviewer => LlmRole.Review,
        ModelRole.Analysis => LlmRole.Analysis,
        ModelRole.Summarizer => LlmRole.Summarizer,
        _ => LlmRole.Code,
    };

    /// <summary>
    /// SPEC-149 RF-003: user model pick at start-task pins only these
    /// roles — every other role always follows the router.
    /// </summary>
    public static bool IsPinnable(LlmRole role) =>
        role is LlmRole.Code or LlmRole.Chat;
}

/// <summary>
/// SPEC-149 RF-001: one candidate of a role's ordered routing list
/// (<c>routing_rules_v1</c>). <see cref="BudgetTier"/>=<c>cheap</c> gains
/// priority when the org budget runs low; <see cref="Internal"/> marks
/// providers allowed under <c>data_residency=internal-only</c>.
/// </summary>
public sealed record RouteCandidate(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("provider")] string? Provider = null,
    [property: JsonPropertyName("max_cost_per_1m")]
        decimal? MaxCostPer1M = null,
    [property: JsonPropertyName("weight")] int? Weight = null,
    [property: JsonPropertyName("budget_tier")] string? BudgetTier = null,
    [property: JsonPropertyName("internal")] bool Internal = false,
    [property: JsonPropertyName("api_key_ref")] string? ApiKeyRef = null);

/// <summary>
/// SPEC-149 RF-001: org routing document stored under
/// <c>org_settings["routing_rules"]</c>. Empty/absent ⇒ pass-through
/// (comportamento idêntico ao de hoje — guardrail de compat).
/// </summary>
public sealed record RoutingRulesDocument
{
    /// <summary>Schema version — only <c>routing_rules_v1</c> is accepted.</summary>
    public const string CurrentVersion = "routing_rules_v1";

    /// <summary>Schema version field.</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = CurrentVersion;

    /// <summary>role wire name → ordered candidate list.</summary>
    [JsonPropertyName("rules")]
    public Dictionary<string, List<RouteCandidate>> Rules { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Admin hard allowlist — empty = unrestricted.</summary>
    [JsonPropertyName("allowed_models")]
    public List<string> AllowedModels { get; init; } = [];

    /// <summary>Admin deny list — wins over everything.</summary>
    [JsonPropertyName("deny_models")]
    public List<string> DenyModels { get; init; } = [];

    /// <summary>Optional legacy→canonical role remap.</summary>
    [JsonPropertyName("default_role_map")]
    public Dictionary<string, string> DefaultRoleMap { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Budget threshold % — below it, cheap candidates go first.</summary>
    [JsonPropertyName("cheap_when_below_pct")]
    public int? CheapWhenBelowPct { get; init; }

    /// <summary>Circuit-open cooldown seconds (per provider:model).</summary>
    [JsonPropertyName("circuit_open_sec")]
    public int? CircuitOpenSec { get; init; }

    /// <summary><c>internal-only</c> ⇒ external candidates never route.</summary>
    [JsonPropertyName("data_residency")]
    public string? DataResidency { get; init; }

    /// <summary>True when no role carries candidates (pass-through).</summary>
    [JsonIgnore]
    public bool IsEmpty => Rules.Values.All(v => v.Count == 0);

    /// <summary>Ordered candidates for a role wire name (null = none).</summary>
    public IReadOnlyList<RouteCandidate>? CandidatesFor(string roleWire)
    {
        if (Rules.TryGetValue(roleWire, out var list) && list.Count > 0)
        {
            return list;
        }

        if (DefaultRoleMap.TryGetValue(roleWire, out var mapped)
            && Rules.TryGetValue(mapped, out var mappedList)
            && mappedList.Count > 0)
        {
            return mappedList;
        }

        return null;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Serializes the document for storage (kv value).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Parses + validates a <c>routing_rules_v1</c> document.
    /// <paramref name="json"/> null/empty ⇒ <c>null</c> (no rules).
    /// Throws <see cref="RoutingRulesException"/> on schema violations.
    /// </summary>
    public static RoutingRulesDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        RoutingRulesDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<RoutingRulesDocument>(
                json, Json);
        }
        catch (JsonException e)
        {
            throw new RoutingRulesException($"invalid JSON: {e.Message}");
        }

        return doc is null ? null : Validate(doc);
    }

    /// <summary>Validates a parsed document against <c>routing_rules_v1</c>.</summary>
    public static RoutingRulesDocument Validate(RoutingRulesDocument doc)
    {
        if (!string.Equals(doc.Version, CurrentVersion,
                StringComparison.Ordinal))
        {
            throw new RoutingRulesException(
                $"version must be \"{CurrentVersion}\"");
        }

        foreach (var (role, candidates) in doc.Rules)
        {
            if (LlmRoles.FromWire(role) is null)
            {
                throw new RoutingRulesException(
                    $"unknown role '{role}'");
            }

            foreach (var c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c.Model))
                {
                    throw new RoutingRulesException(
                        $"rules.{role}: model is required");
                }

                if (c.Weight is < 0)
                {
                    throw new RoutingRulesException(
                        $"rules.{role}: weight must be >= 0");
                }

                if (c.BudgetTier is not null
                    && !string.Equals(c.BudgetTier, "cheap",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new RoutingRulesException(
                        $"rules.{role}: budget_tier must be \"cheap\"");
                }
            }
        }

        if (doc.DataResidency is not null
            && !string.Equals(doc.DataResidency, "internal-only",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new RoutingRulesException(
                "data_residency must be \"internal-only\"");
        }

        if (doc.CheapWhenBelowPct is < 0 or > 100)
        {
            throw new RoutingRulesException(
                "cheap_when_below_pct must be 0-100");
        }

        if (doc.CircuitOpenSec is < 1)
        {
            throw new RoutingRulesException(
                "circuit_open_sec must be >= 1");
        }

        return doc;
    }
}

/// <summary>Schema violation of a routing rules document (→ 400).</summary>
public sealed class RoutingRulesException(string detail)
    : Exception(detail);

/// <summary>
/// SPEC-149 RF-001: context of one route resolution — who is calling
/// (org for rules/budget, user for BYOK refs) and the user-picked model
/// (pin — only honored on <see cref="LlmRoles.IsPinnable"/> roles).
/// </summary>
public sealed record LlmRouteContext(
    Guid? ConversationId = null,
    Guid? OrgId = null,
    string? UserId = null,
    string? PreferredModel = null);

/// <summary>
/// Resolution snapshot carried inside <see cref="ModelSelection.Route"/> —
/// the evaluated candidate order (post policy/circuit/budget) that
/// <see cref="IModelRouter.NextFallbackAsync"/> walks on provider failure.
/// </summary>
public sealed record RoutePlan(
    string Role,
    Guid? OrgId,
    string? UserId,
    string? PreferredModel,
    IReadOnlyList<RouteCandidate> Candidates,
    bool RulesActive,
    int CircuitOpenSec);

/// <summary>
/// SPEC-149 RF-002: no candidate eligible for the role — surfaced as
/// <c>llm_no_route</c> (HTTP 503 semantic). Never silently resolved to an
/// arbitrary model outside the configured list.
/// </summary>
public sealed class LlmNoRouteException(string role, string detail)
    : LlmCallException(
        LlmErrorKind.ModelUnavailable,
        $"llm_no_route: role={role} — {detail}", 503)
{
    /// <summary>Role wire name that failed to resolve.</summary>
    public string Role { get; } = role;
}

/// <summary>
/// SPEC-149: <c>Router:*</c> options — circuit cooldown, budget threshold
/// and failover timeout (0 = inherit <see cref="AgentLlmOptions.TimeoutSeconds"/>).
/// </summary>
public sealed class RouterOptions
{
    /// <summary>Section name (<c>SANDCASTLE_Router__*</c>).</summary>
    public const string Section = "Router";

    /// <summary>Seconds a failed candidate stays circuit-open (default 60).</summary>
    public int CircuitOpenSec { get; set; } = 60;

    /// <summary>Budget remaining % below which cheap-tier candidates lead.</summary>
    public int CheapWhenBelowPct { get; set; } = 20;

    /// <summary>
    /// Per-attempt failover bound in seconds — 0 inherits the provider
    /// timeout; the classified timeout still drives fallback (429/5xx/timeout).
    /// </summary>
    public int FailoverTimeoutSec { get; set; }
}

/// <summary>
/// SPEC-149: org routing-rules store — backed by
/// <c>OrganizationRecord.SettingsJson["routing_rules"]</c> (kv pattern,
/// mesma chave usada pelo endpoint admin).
/// </summary>
public interface IRoutingRulesService
{
    /// <summary>Chave do kv em org_settings.</summary>
    const string SettingsKey = "routing_rules";

    /// <summary>Org's document — null when absent/empty/corrupt (fail-open ⇒ pass-through).</summary>
    Task<RoutingRulesDocument?> GetAsync(Guid? orgId, CancellationToken ct);

    /// <summary>
    /// Persists (null/empty ⇒ clears ⇒ pass-through) and audits
    /// <c>org.routing_rules.update</c>. Validates before writing.
    /// </summary>
    Task SetAsync(
        Guid orgId, RoutingRulesDocument? doc, string? actorUserId,
        CancellationToken ct);

    /// <summary>Conversation → org/user/preferred-model ctx for the router.</summary>
    Task<LlmRouteContext?> ContextForConversationAsync(
        Guid conversationId, CancellationToken ct);
}

/// <summary>SPEC-149 RF-004: role×model usage breakdown row.</summary>
public sealed record LlmUsageRow(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("tokens")] long Tokens,
    [property: JsonPropertyName("cost_usd")] decimal CostUsd);

/// <summary>
/// <c>GET /api/v1/usage/llm?by=role</c> payload — breakdown agrupado por
/// <paramref name="By"/> (hoje só <c>role</c>) sobre TokenEvents do período.
/// </summary>
public sealed record LlmUsageBreakdown(
    [property: JsonPropertyName("by")] string By,
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("rows")] IReadOnlyList<LlmUsageRow> Rows);