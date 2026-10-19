using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Application.Events;

/// <summary>Upstream sort order for event search.</summary>
public enum EventSortOrder
{
    /// <summary>Ascending by timestamp (default).</summary>
    Timestamp,

    /// <summary>Descending by timestamp.</summary>
    TimestampDesc,
}

/// <summary>
/// Event search query — mirrors upstream filters
/// (<c>kind__eq</c>, <c>timestamp__gte</c>, <c>timestamp__lt</c>, <c>sort_order</c>,
/// <c>page_id</c>, <c>limit</c>).
/// </summary>
/// <param name="KindEq">Exact kind match (e.g. <c>"MessageEvent"</c>).</param>
/// <param name="TimestampGte">Inclusive lower bound on timestamp.</param>
/// <param name="TimestampLt">Exclusive upper bound on timestamp.</param>
/// <param name="Sort">Sort order (default ascending).</param>
/// <param name="PageId">Opaque cursor from a previous <see cref="EventPage.NextPageId"/>.</param>
/// <param name="Limit">Page size (max 100, upstream parity).</param>
public sealed record EventQuery(
    string? KindEq = null,
    DateTimeOffset? TimestampGte = null,
    DateTimeOffset? TimestampLt = null,
    EventSortOrder Sort = EventSortOrder.Timestamp,
    string? PageId = null,
    int Limit = 100)
{
    /// <summary>Effective page size clamped to the upstream ceiling.</summary>
    public int EffectiveLimit => Math.Clamp(Limit, 1, 100);
}

/// <summary>
/// A page of events — upstream <c>EventPage{items, next_page_id}</c> shape.
/// </summary>
/// <param name="Items">Events in this page.</param>
/// <param name="NextPageId">Opaque cursor for the next page; <see langword="null"/> when exhausted.</param>
public sealed record EventPage(IReadOnlyList<EventBase> Items, string? NextPageId);