namespace OpenHandsNET.Domain.Events;

/// <summary>Streaming token chunk with token ids (kind <c>TokenEvent</c>).</summary>
public sealed record TokenEvent : EventBase
{
    /// <inheritdoc />

    /// <summary>Prompt token ids.</summary>
    public IReadOnlyList<int> PromptTokenIds { get; init; } = [];

    /// <summary>Response token ids.</summary>
    public IReadOnlyList<int> ResponseTokenIds { get; init; } = [];
}
