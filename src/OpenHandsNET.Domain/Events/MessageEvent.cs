using System.Text.Json;

namespace OpenHandsNET.Domain.Events;

/// <summary>Agent or user message (kind <c>MessageEvent</c>).</summary>
public sealed record MessageEvent : EventBase
{
    /// <inheritdoc />

    /// <summary>Exact LLM message payload for this event.</summary>
    public required JsonElement LlmMessage { get; init; }

    /// <summary>LLM completion/response id that produced the message.</summary>
    public string? LlmResponseId { get; init; }

    /// <summary>Skills activated by this message.</summary>
    public IReadOnlyList<string> ActivatedSkills { get; init; } = [];

    /// <summary>Content appended by agent context (path-scoped rules).</summary>
    public JsonElement? ExtendedContent { get; init; }

    /// <summary>Display name of the sender (user identity).</summary>
    public string? Sender { get; init; }

    /// <summary>Critic evaluation attached to the message.</summary>
    public JsonElement? CriticResult { get; init; }
}