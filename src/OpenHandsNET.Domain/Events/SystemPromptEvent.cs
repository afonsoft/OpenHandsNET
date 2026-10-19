using System.Text.Json;

namespace OpenHandsNET.Domain.Events;

/// <summary>System prompt + tool definitions (kind <c>SystemPromptEvent</c>).</summary>
public sealed record SystemPromptEvent : EventBase
{
    /// <summary>Creates an agent-sourced system prompt event.</summary>
    public SystemPromptEvent() => Source = EventSource.Agent;

    /// <inheritdoc />

    /// <summary>Static system prompt text content.</summary>
    public required JsonElement SystemPrompt { get; init; }

    /// <summary>Tool definitions available to the agent.</summary>
    public required JsonElement Tools { get; init; }

    /// <summary>Optional per-conversation dynamic context.</summary>
    public JsonElement? DynamicContext { get; init; }
}