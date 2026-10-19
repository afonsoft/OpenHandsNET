using System.Text.Json;

namespace OpenHandsNET.Domain.Events;

/// <summary>Base for events that respond to a tool call.</summary>
public abstract record ObservationBaseEvent : EventBase
{
    /// <summary>Creates an environment-sourced observation.</summary>
    protected ObservationBaseEvent() => Source = EventSource.Environment;

    /// <summary>Tool name this observation responds to.</summary>
    public required string ToolName { get; init; }

    /// <summary>Tool call id this observation responds to.</summary>
    public required string ToolCallId { get; init; }
}