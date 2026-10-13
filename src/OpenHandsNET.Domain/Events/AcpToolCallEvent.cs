using System.Text.Json;

namespace OpenHandsNET.Domain.Events;

/// <summary>Agent Client Protocol tool call (kind <c>ACPToolCallEvent</c>).</summary>
public sealed record AcpToolCallEvent : EventBase
{
    /// <summary>Creates an agent-sourced ACP tool call.</summary>
    public AcpToolCallEvent() => Source = EventSource.Agent;

    /// <inheritdoc />

    /// <summary>ACP tool call id.</summary>
    public required string ToolCallId { get; init; }

    /// <summary>Human-readable title.</summary>
    public required string Title { get; init; }

    /// <summary>Call status.</summary>
    public string? Status { get; init; }

    /// <summary>Tool kind.</summary>
    public string? ToolKind { get; init; }

    /// <summary>Raw input payload.</summary>
    public JsonElement? RawInput { get; init; }

    /// <summary>Raw output payload.</summary>
    public JsonElement? RawOutput { get; init; }

    /// <summary>ACP content blocks.</summary>
    public JsonElement? Content { get; init; }

    /// <summary>Whether the call errored.</summary>
    public bool IsError { get; init; }
}
