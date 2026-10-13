using System.Text.Json;

namespace OpenHandsNET.Domain.Events;

/// <summary>Tool-call result (kind <c>ObservationEvent</c>).</summary>
public sealed record ObservationEvent : ObservationBaseEvent
{
    /// <inheritdoc />

    /// <summary>Observation payload sent to the LLM.</summary>
    public required JsonElement Observation { get; init; }

    /// <summary>Action id this observation responds to.</summary>
    public required string ActionId { get; init; }

    /// <summary>Content added by agent context (path-scoped rules).</summary>
    public JsonElement? ExtendedContent { get; init; }
}
