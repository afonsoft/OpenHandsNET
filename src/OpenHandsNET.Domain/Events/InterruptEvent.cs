namespace OpenHandsNET.Domain.Events;

/// <summary>User interrupt (kind <c>InterruptEvent</c>).</summary>
public sealed record InterruptEvent : EventBase
{
    /// <summary>Creates a user-sourced interrupt.</summary>
    public InterruptEvent() => Source = EventSource.User;
}
