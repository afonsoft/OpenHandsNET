using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenHandsNET.Domain.Identity;

/// <summary>
/// Plan limits parsed from <c>PlanRecord.LimitsJson</c> (SPEC-20 RF-001).
/// <c>null</c> on a field means unlimited. Parse is fail-open — malformed
/// JSON or invalid values yield <see cref="Empty"/> (a config error must
/// never take the product down).
/// </summary>
public sealed record PlanLimits(
    [property: JsonPropertyName("max_conversations_per_day")]
    int? MaxConversationsPerDay = null,
    [property: JsonPropertyName("max_concurrent_runs")]
    int? MaxConcurrentRuns = null,
    [property: JsonPropertyName("max_tokens_per_month")]
    long? MaxTokensPerMonth = null,
    [property: JsonPropertyName("max_sandbox_seconds_per_month")]
    long? MaxSandboxSecondsPerMonth = null,
    [property: JsonPropertyName("max_sandbox_profile")]
    string? MaxSandboxProfile = null)
{
    /// <summary>Unlimited plan.</summary>
    public static PlanLimits Empty { get; } = new();

    /// <summary>Serializes to the canonical LimitsJson shape.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Parses <paramref name="json"/>; null/empty/malformed →
    /// <see cref="Empty"/>. Negative numbers are treated as unset.
    /// </summary>
    public static PlanLimits Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            var limits = JsonSerializer.Deserialize<PlanLimits>(json, JsonOptions)
                ?? Empty;
            return limits with
            {
                MaxConversationsPerDay = NonNegative(limits.MaxConversationsPerDay),
                MaxConcurrentRuns = NonNegative(limits.MaxConcurrentRuns),
                MaxTokensPerMonth = NonNegative(limits.MaxTokensPerMonth),
                MaxSandboxSecondsPerMonth =
                    NonNegative(limits.MaxSandboxSecondsPerMonth),
            };
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static int? NonNegative(int? v) => v is >= 0 ? v : null;

    private static long? NonNegative(long? v) => v is >= 0 ? v : null;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}