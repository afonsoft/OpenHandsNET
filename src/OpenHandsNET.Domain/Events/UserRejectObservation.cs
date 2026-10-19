namespace OpenHandsNET.Domain.Events;

/// <summary>User/hook rejection of a pending action (kind <c>UserRejectObservation</c>).</summary>
public sealed record UserRejectObservation : ObservationBaseEvent
{
    /// <inheritdoc />

    /// <summary>Human-readable rejection reason.</summary>
    public string? RejectionReason { get; init; }

    /// <summary>Who rejected: <c>user</c> | <c>hook</c>.</summary>
    public string? RejectionSource { get; init; }

    /// <summary>Action id being rejected.</summary>
    public required string ActionId { get; init; }
}