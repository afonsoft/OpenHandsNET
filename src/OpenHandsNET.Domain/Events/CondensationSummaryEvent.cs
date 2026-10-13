namespace OpenHandsNET.Domain.Events;

/// <summary>Summary emitted after condensation (kind <c>CondensationSummaryEvent</c>).</summary>
public sealed record CondensationSummaryEvent : EventBase
{
    /// <summary>Creates an environment-sourced summary.</summary>
    public CondensationSummaryEvent() => Source = EventSource.Environment;

    /// <inheritdoc />

    /// <summary>Summary text.</summary>
    public required string Summary { get; init; }
}
