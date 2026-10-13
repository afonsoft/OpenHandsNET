namespace OpenHandsNET.Domain.Events;

/// <summary>Server-level error (MCP/WebSocket/infrastructure; kind <c>ServerErrorEvent</c>).</summary>
public sealed record ServerErrorEvent : EventBase
{
    /// <inheritdoc />

    /// <summary>Error code — typically an error type.</summary>
    public required string Code { get; init; }

    /// <summary>Error details.</summary>
    public required string Detail { get; init; }
}
