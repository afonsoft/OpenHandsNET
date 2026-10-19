using System.Text.Json.Serialization;

namespace OpenHandsNET.Domain.Events;

/// <summary>
/// Completion usage marker (kind <c>TokenEvent</c>). Upstream carries raw
/// token ids; OpenHandsNET additionally records the provider usage counters
/// (additive fields — the <c>kind</c> wire contract is unchanged).
/// </summary>
public sealed record TokenEvent : EventBase
{
    /// <inheritdoc />

    /// <summary>Prompt token ids (when the provider streams them).</summary>
    public IReadOnlyList<int> PromptTokenIds { get; init; } = [];

    /// <summary>Response token ids (when the provider streams them).</summary>
    public IReadOnlyList<int> ResponseTokenIds { get; init; } = [];

    /// <summary>Prompt tokens billed (provider usage).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PromptTokens { get; init; }

    /// <summary>Completion tokens billed (provider usage).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CompletionTokens { get; init; }

    /// <summary>Estimated cost in USD when known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CostUsd { get; init; }

    /// <summary>Model that produced this usage (SPEC-23 RF-004).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    /// <summary>Routing role — worker/summarizer/planner (SPEC-23 RF-004).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; init; }
}