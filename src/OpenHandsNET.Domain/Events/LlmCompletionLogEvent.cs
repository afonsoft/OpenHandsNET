namespace OpenHandsNET.Domain.Events;

/// <summary>Persisted LLM completion log reference (kind <c>LLMCompletionLogEvent</c>).</summary>
public sealed record LlmCompletionLogEvent : EventBase
{
    /// <summary>Creates an environment-sourced log event.</summary>
    public LlmCompletionLogEvent() => Source = EventSource.Environment;

    /// <inheritdoc />

    /// <summary>Log file name.</summary>
    public required string Filename { get; init; }

    /// <summary>Serialized log payload.</summary>
    public required string LogData { get; init; }

    /// <summary>Model name.</summary>
    public required string ModelName { get; init; }

    /// <summary>Usage id for token accounting.</summary>
    public required string UsageId { get; init; }
}