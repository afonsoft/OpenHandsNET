namespace OpenHandsNET.Domain.Events;

/// <summary>Request for context condensation (kind <c>CondensationRequest</c>).</summary>
public sealed record CondensationRequest : EventBase
{
    /// <summary>Creates an environment-sourced request.</summary>
    public CondensationRequest() => Source = EventSource.Environment;
}
