using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Eventing;

/// <summary>
/// Append-only EF Core event store implementing <see cref="IEventService"/>:
/// persists every event and fans it out to the in-process <see cref="IEventBus"/>.
/// </summary>
public sealed class EventStore(AppDbContext db, IEventBus bus) : IEventService
{
    /// <inheritdoc />
    public async ValueTask<EventBase> AppendAsync(
        Guid conversationId, EventBase e, CancellationToken ct)
    {
        var record = new EventRecord
        {
            Id = e.Id,
            ConversationId = conversationId,
            Kind = e.EventKind,
            Source = e.Source.ToString().ToLowerInvariant(),
            TimestampTicks = e.Timestamp.UtcTicks,
            ParentId = e.ParentId,
            Payload = JsonSerializer.Serialize(e, typeof(EventBase), EventJson.Options),
        };
        db.Events.Add(record);
        await db.SaveChangesAsync(ct);
        Telemetry.OpenNetTelemetry.EventsAppended
            .WithLabels(e.EventKind).Inc();
        await bus.PublishAsync(conversationId, e, ct);
        return e;
    }

    /// <inheritdoc />
    public async Task<EventPage> SearchAsync(
        Guid conversationId, EventQuery q, CancellationToken ct)
    {
        var query = ApplyFilters(db.Events.Where(r => r.ConversationId == conversationId), q);

        var desc = q.Sort == EventSortOrder.TimestampDesc;
        if (CursorPaginator.TryDecode(q.PageId, out var curTs, out var curId))
        {
            // String.Compare: SQLite/PG traduzem para comparação lexicográfica.
            query = desc
                ? query.Where(r => r.TimestampTicks < curTs
                    || (r.TimestampTicks == curTs && string.Compare(r.Id, curId) < 0))
                : query.Where(r => r.TimestampTicks > curTs
                    || (r.TimestampTicks == curTs && string.Compare(r.Id, curId) > 0));
        }

        var limit = q.EffectiveLimit;
        var ordered = desc
            ? query.OrderByDescending(r => r.TimestampTicks).ThenByDescending(r => r.Id)
            : query.OrderBy(r => r.TimestampTicks).ThenBy(r => r.Id);
        var rows = await ordered.Take(limit + 1).ToListAsync(ct);

        var hasMore = rows.Count > limit;
        var page = hasMore ? rows.Take(limit).ToList() : rows;
        var nextPageId = hasMore && page.Count > 0
            ? CursorPaginator.Encode(page[^1].TimestampTicks, page[^1].Id)
            : null;

        return new EventPage(page.Select(Deserialize).ToList(), nextPageId);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(
        Guid conversationId, EventQuery q, CancellationToken ct)
    {
        return await ApplyFilters(
            db.Events.Where(r => r.ConversationId == conversationId), q)
            .LongCountAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EventBase>> GetBatchAsync(
        Guid conversationId, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        var rows = await db.Events
            .Where(r => r.ConversationId == conversationId && wanted.Contains(r.Id))
            .ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
        return ids
            .Where(byId.ContainsKey)
            .Select(id => Deserialize(byId[id]))
            .ToList();
    }

    /// <inheritdoc />
    public IAsyncEnumerable<EventBase> SubscribeAsync(Guid conversationId, CancellationToken ct) =>
        bus.SubscribeAsync(conversationId, ct);

    private static IQueryable<EventRecord> ApplyFilters(IQueryable<EventRecord> q, EventQuery query)
    {
        if (query.KindEq is not null)
        {
            q = q.Where(r => r.Kind == query.KindEq);
        }

        if (query.TimestampGte is { } gte)
        {
            q = q.Where(r => r.TimestampTicks >= gte.UtcTicks);
        }

        if (query.TimestampLt is { } lt)
        {
            q = q.Where(r => r.TimestampTicks < lt.UtcTicks);
        }

        return q;
    }

    private static EventBase Deserialize(EventRecord r) =>
        JsonSerializer.Deserialize<EventBase>(r.Payload, EventJson.Options)
            ?? throw new InvalidOperationException($"Undecodable event payload id={r.Id}");
}