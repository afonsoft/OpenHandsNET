using System.Text.Json;

namespace OpenHandsNET.Domain.Events;

/// <summary>Conversation state field update (kind <c>ConversationStateUpdateEvent</c>).</summary>
public sealed record ConversationStateUpdateEvent : EventBase
{
    /// <summary>Creates an environment-sourced state update.</summary>
    public ConversationStateUpdateEvent() => Source = EventSource.Environment;

    /// <inheritdoc />

    /// <summary>State key being updated (e.g. <c>execution_status</c>).</summary>
    public required string Key { get; init; }

    /// <summary>New state value.</summary>
    public JsonElement? Value { get; init; }
}