using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Application.Events;

/// <summary>
/// In-process fan-out for appended events (feeds SignalR, webhooks, subscribers).
/// </summary>
public interface IEventBus
{
    /// <summary>Publishes a persisted event to conversation subscribers and the global stream.</summary>
    ValueTask PublishAsync(Guid conversationId, EventBase e, CancellationToken ct);

    /// <summary>
    /// Streams events appended to the conversation after subscription, in order.
    /// </summary>
    IAsyncEnumerable<EventBase> SubscribeAsync(Guid conversationId, CancellationToken ct);

    /// <summary>
    /// Global stream of every appended event (drives webhooks, cross-conversation hooks).
    /// </summary>
    IAsyncEnumerable<EventEnvelope> SubscribeAllAsync(CancellationToken ct);
}
