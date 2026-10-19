using System.Text.Json;

namespace OpenHandsNET.Domain.Events;

/// <summary>Agent tool-call action (kind <c>ActionEvent</c>).</summary>
public sealed record ActionEvent : EventBase
{
    /// <summary>Creates an agent-sourced action event.</summary>
    public ActionEvent() => Source = EventSource.Agent;

    /// <inheritdoc />

    /// <summary>Agent thought content blocks preceding the action.</summary>
    public JsonElement? Thought { get; init; }

    /// <summary>Intermediate reasoning content from reasoning models.</summary>
    public string? ReasoningContent { get; init; }

    /// <summary>Provider thinking blocks (e.g. Anthropic thinking).</summary>
    public JsonElement? ThinkingBlocks { get; init; }

    /// <summary>OpenAI Responses reasoning item.</summary>
    public JsonElement? ResponsesReasoningItem { get; init; }

    /// <summary>Single tool call returned by the LLM (null when non-executable).</summary>
    public JsonElement? Action { get; init; }

    /// <summary>Name of the tool being called.</summary>
    public required string ToolName { get; init; }

    /// <summary>Unique id returned by the LLM API for this tool call.</summary>
    public required string ToolCallId { get; init; }

    /// <summary>Raw tool call as received from the LLM response.</summary>
    public JsonElement? ToolCall { get; init; }

    /// <summary>LLM completion/response id that generated this action.</summary>
    public required string LlmResponseId { get; init; }

    /// <summary>LLM-assessed security risk of this action.</summary>
    public JsonElement? SecurityRisk { get; init; }
}