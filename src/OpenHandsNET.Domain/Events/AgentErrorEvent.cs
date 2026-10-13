namespace OpenHandsNET.Domain.Events;

/// <summary>Scaffold/agent error surfaced as observation (kind <c>AgentErrorEvent</c>).</summary>
public sealed record AgentErrorEvent : ObservationBaseEvent
{
    /// <summary>Creates an agent-sourced error event.</summary>
    public AgentErrorEvent() => Source = EventSource.Agent;

    /// <inheritdoc />

    /// <summary>Error message from the scaffold.</summary>
    public required string Error { get; init; }
}
