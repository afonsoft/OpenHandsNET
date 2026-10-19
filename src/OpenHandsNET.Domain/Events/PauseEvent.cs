namespace OpenHandsNET.Domain.Events;

/// <summary>User pause request (kind <c>PauseEvent</c>).</summary>
public sealed record PauseEvent : EventBase
{
    /// <summary>Creates a user-sourced pause.</summary>
    public PauseEvent() => Source = EventSource.User;
}