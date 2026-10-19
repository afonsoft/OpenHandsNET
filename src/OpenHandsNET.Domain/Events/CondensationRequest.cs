namespace OpenHandsNET.Domain.Events;

/// <summary>Request for context condensation (kind <c>CondensationRequest</c>).</summary>
public sealed record CondensationRequest : EventBase
{
    /// <summary>Creates an environment-sourced request.</summary>
    public CondensationRequest() => Source = EventSource.Environment;

    /// <inheritdoc />

    /// <summary>
    /// What triggered the request (SPEC-22): <c>threshold</c>, <c>mid_turn</c>,
    /// <c>overflow</c>, <c>stop_length</c>, <c>manual</c>, <c>model_switch</c>.
    /// </summary>
    public string? Trigger { get; init; }

    /// <summary>Optional user instructions for a manual compaction.</summary>
    public string? Instructions { get; init; }

    /// <summary>Estimated tokens in context when the request fired.</summary>
    public int? TokensBefore { get; init; }
}