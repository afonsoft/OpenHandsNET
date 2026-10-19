namespace OpenHandsNET.Domain.Events;

/// <summary>Incremental LLM delta chunk (kind <c>StreamingDeltaEvent</c>).</summary>
public sealed record StreamingDeltaEvent : EventBase
{
    /// <summary>Creates an agent-sourced delta event.</summary>
    public StreamingDeltaEvent() => Source = EventSource.Agent;

    /// <inheritdoc />

    /// <summary>Text delta.</summary>
    public string? Content { get; init; }

    /// <summary>Reasoning delta.</summary>
    public string? ReasoningContent { get; init; }
}