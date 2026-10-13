using System.Text.Json;

namespace OpenHandsNET.Domain.Events;

/// <summary>Lifecycle hook execution record (kind <c>HookExecutionEvent</c>).</summary>
public sealed record HookExecutionEvent : EventBase
{
    /// <summary>Creates a hook-sourced event.</summary>
    public HookExecutionEvent() => Source = EventSource.Hook;

    /// <inheritdoc />

    /// <summary>Hook lifecycle type that ran.</summary>
    public required string HookEventType { get; init; }

    /// <summary>Hook command executed.</summary>
    public required string HookCommand { get; init; }

    /// <summary>Tool associated with the hook, when any.</summary>
    public string? ToolName { get; init; }

    /// <summary>Whether the hook succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Whether the hook blocked the action.</summary>
    public bool Blocked { get; init; }

    /// <summary>Hook process exit code.</summary>
    public int ExitCode { get; init; }

    /// <summary>Captured stdout.</summary>
    public string Stdout { get; init; } = "";

    /// <summary>Captured stderr.</summary>
    public string Stderr { get; init; } = "";

    /// <summary>Block/failure reason.</summary>
    public string? Reason { get; init; }

    /// <summary>Context injected by the hook.</summary>
    public string? AdditionalContext { get; init; }

    /// <summary>Error detail on failure.</summary>
    public string? Error { get; init; }

    /// <summary>Related action id.</summary>
    public string? ActionId { get; init; }

    /// <summary>Related message id.</summary>
    public string? MessageId { get; init; }

    /// <summary>Raw hook input payload.</summary>
    public JsonElement? HookInput { get; init; }
}
