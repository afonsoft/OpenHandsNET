using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenHandsNET.Application.Agent;

/// <summary>Permission decision for a tool call (SPEC-29).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PermissionDecision>))]
public enum PermissionDecision
{
    /// <summary>Executa sem confirmação (mesmo tools Risky).</summary>
    Allow,

    /// <summary>Removida do catálogo — o modelo não pode chamá-la.</summary>
    Deny,

    /// <summary>Exige confirmação do usuário por chamada.</summary>
    Ask,
}

/// <summary>
/// One <c>allow</c>/<c>deny</c>/<c>ask</c> rule (SPEC-29 RF-001). Pattern is a
/// tool name with glob support: <c>*</c> matches any suffix
/// (<c>mcp__*</c>, <c>file_*</c>, <c>bash</c>).
/// </summary>
public sealed record AgentPermissionRule(
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("decision")] PermissionDecision Decision)
{
    /// <summary>Literal prefix before the first <c>*</c> (specificity key).</summary>
    public int LiteralPrefixLength
    {
        get
        {
            var i = Pattern.IndexOf('*');
            return i < 0 ? Pattern.Length : i;
        }
    }

    /// <summary>Case-insensitive glob match on the tool name.</summary>
    public bool Matches(string toolName)
    {
        if (Pattern.IndexOf('*') < 0)
        {
            return string.Equals(Pattern, toolName,
                StringComparison.OrdinalIgnoreCase);
        }

        var regex = "^" + Regex.Escape(Pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(toolName, regex,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

/// <summary>
/// Resolves the effective permission for a tool (SPEC-29 RF-001/RF-002).
/// Precedence: org <c>deny</c> floor → plan-mode overlay → conversation rules
/// → org rules → instance rules → <see langword="null"/> (caller default:
/// Risky+confirmation ⇒ ask, else allow). Inside one list, the rule with the
/// longest literal prefix wins.
/// </summary>
public sealed class PermissionEvaluator
{
    private static readonly IReadOnlyList<AgentPermissionRule> PlanOverlay =
    [
        new("file_write", PermissionDecision.Deny),
        new("file_edit", PermissionDecision.Deny),
        new("git_commit", PermissionDecision.Deny),
        new("bash", PermissionDecision.Ask),
        new("browser", PermissionDecision.Ask),
    ];

    private readonly IReadOnlyList<AgentPermissionRule> _org;
    private readonly IReadOnlyList<AgentPermissionRule> _conversation;
    private readonly IReadOnlyList<AgentPermissionRule> _instance;
    private readonly bool _planMode;

    /// <summary>Builds the merged evaluator (lists already specificity-sorted).</summary>
    public PermissionEvaluator(
        IReadOnlyList<AgentPermissionRule>? orgRules,
        IReadOnlyList<AgentPermissionRule>? conversationRules,
        IReadOnlyList<AgentPermissionRule>? instanceRules,
        bool planMode)
    {
        _org = Sort(orgRules);
        _conversation = Sort(conversationRules);
        _instance = Sort(instanceRules);
        _planMode = planMode;
    }

    /// <summary>
    /// Decision for <paramref name="toolName"/> — <see langword="null"/> means
    /// "no rule matched; fall back to the Risky/confirmation default".
    /// </summary>
    public PermissionDecision? Decide(string toolName)
    {
        // Org deny is an absolute floor — conversation/instance can't un-deny.
        if (Match(_org, toolName) is { Decision: PermissionDecision.Deny })
        {
            return PermissionDecision.Deny;
        }

        // Plan mode is a user-chosen overlay — wins over explicit allows.
        if (_planMode && Match(PlanOverlay, toolName) is { } planRule)
        {
            return planRule.Decision;
        }

        return Match(_conversation, toolName)?.Decision
            ?? Match(_org, toolName)?.Decision
            ?? Match(_instance, toolName)?.Decision;
    }

    private static AgentPermissionRule? Match(
        IReadOnlyList<AgentPermissionRule> rules, string toolName)
    {
        foreach (var r in rules)
        {
            if (r.Matches(toolName))
            {
                return r;
            }
        }

        return null;
    }

    private static IReadOnlyList<AgentPermissionRule> Sort(
        IReadOnlyList<AgentPermissionRule>? rules) =>
        rules is null || rules.Count == 0
            ? []
            : rules.OrderByDescending(r => r.LiteralPrefixLength).ToList();
}

/// <summary>JSON helpers for the permission-rule columns/settings.</summary>
public static class PermissionRules
{
    /// <summary>Parses a JSON array of rules; invalid input yields [].</summary>
    public static IReadOnlyList<AgentPermissionRule> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<AgentPermissionRule>>(
                json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Serializes rules (snake_case) for the JSON columns.</summary>
    public static string ToJson(IReadOnlyList<AgentPermissionRule> rules) =>
        JsonSerializer.Serialize(rules, JsonOptions);

    /// <summary>
    /// Extracts <c>agent_permissions</c> rules from an org
    /// <c>settings_json</c> blob (SPEC-29 RF-003 floor).
    /// </summary>
    public static IReadOnlyList<AgentPermissionRule> ParseOrgSettings(
        string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.TryGetProperty("agent_permissions", out var el)
                && el.ValueKind == JsonValueKind.Array)
            {
                return el.Deserialize<List<AgentPermissionRule>>(JsonOptions)
                    ?? [];
            }
        }
        catch (JsonException)
        {
        }

        return [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };
}