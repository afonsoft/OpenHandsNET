using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Application.Events;

/// <summary>
/// Conversation event stream: append-only persistence, cursored search and
/// realtime subscription (SPEC-02).
/// </summary>
public interface IEventService
{
    /// <summary>Appends an event to the conversation stream and publishes it to subscribers.</summary>
    ValueTask<EventBase> AppendAsync(Guid conversationId, EventBase e, CancellationToken ct);

    /// <summary>Searches events with upstream filters and cursor pagination.</summary>
    Task<EventPage> SearchAsync(Guid conversationId, EventQuery q, CancellationToken ct);

    /// <summary>Counts events matching the query filters (ignores paging).</summary>
    Task<long> CountAsync(Guid conversationId, EventQuery q, CancellationToken ct);

    /// <summary>
    /// Returns the events with the given ids (order of ids preserved).
    /// Ids are the ULID/UUID strings of the events (SPEC-02 lists Guid, but event
    /// ids are ULID strings — corrected to string).
    /// </summary>
    Task<IReadOnlyList<EventBase>> GetBatchAsync(
        Guid conversationId, IReadOnlyCollection<string> ids, CancellationToken ct);

    /// <summary>Streams appended events in append order until cancelled.</summary>
    IAsyncEnumerable<EventBase> SubscribeAsync(Guid conversationId, CancellationToken ct);
}
