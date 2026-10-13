namespace OpenHandsNET.Domain.Events;

/// <summary>Context-window condensation record (kind <c>Condensation</c>).</summary>
public sealed record Condensation : EventBase
{
    /// <summary>Creates an environment-sourced condensation.</summary>
    public Condensation() => Source = EventSource.Environment;

    /// <inheritdoc />

    /// <summary>Event ids forgotten by the condensation.</summary>
    public IReadOnlyList<string> ForgottenEventIds { get; init; } = [];

    /// <summary>LLM-produced summary text.</summary>
    public string? Summary { get; init; }

    /// <summary>Offset where the summary was inserted.</summary>
    public int? SummaryOffset { get; init; }

    /// <summary>LLM response id that produced the summary.</summary>
    public required string LlmResponseId { get; init; }
}
