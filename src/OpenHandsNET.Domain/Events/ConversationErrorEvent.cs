using System.Text.Json;

namespace OpenHandsNET.Domain.Events;

/// <summary>Conversation-level failure (kind <c>ConversationErrorEvent</c>).</summary>
public sealed record ConversationErrorEvent : EventBase
{
    /// <inheritdoc />

    /// <summary>Error code — typically a type name.</summary>
    public required string Code { get; init; }

    /// <summary>Error details.</summary>
    public required string Detail { get; init; }

    /// <summary>Error classification payload.</summary>
    public JsonElement? Classification { get; init; }
}
