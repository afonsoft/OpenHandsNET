namespace OpenHandsNET.Domain.Events;

/// <summary>
/// Autonomous decision audit record (kind <c>decision_auto</c>, SPEC-15 RF-003):
/// in always-allow mode (<c>confirmation_mode=false</c>) a risky action that
/// would otherwise pause for approval is auto-approved and this event makes
/// the choice auditable. Destructive actions still require a human
/// (<see cref="UserRejectObservation"/>/confirmation flow).
/// </summary>
public sealed record DecisionAutoEvent : EventBase
{
    /// <summary>Creates an agent-sourced decision audit event.</summary>
    public DecisionAutoEvent() => Source = EventSource.Agent;

    /// <inheritdoc />

    /// <summary>Id of the <see cref="ActionEvent"/> that was auto-decided.</summary>
    public required string AskedId { get; init; }

    /// <summary>Decision taken (e.g. <c>approve</c>).</summary>
    public required string Chosen { get; init; }

    /// <summary>Why the choice was automatic (e.g. <c>always_allow</c>).</summary>
    public required string Reason { get; init; }
}