using System.Text.Json;
using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// <c>/api/v1/conversation/{id}/events</c> — paridade exata com
/// <c>search-events</c> upstream (filtros, cursor, limit ≤100).
/// </summary>
public static class EventEndpoints
{
    /// <summary>Maps the conversation events route group.</summary>
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/conversation").WithTags("Events");

        group.MapGet("/{id:guid}/events/search", async (
            Guid id, string? kind__eq, DateTimeOffset? timestamp__gte,
            DateTimeOffset? timestamp__lt, string? sort_order,
            string? page_id, int? limit,
            IEventService events, CancellationToken ct) =>
        {
            var sort = sort_order?.Equals("TIMESTAMP_DESC",
                StringComparison.OrdinalIgnoreCase) == true
                ? EventSortOrder.TimestampDesc : EventSortOrder.Timestamp;
            var page = await events.SearchAsync(id, new EventQuery(
                KindEq: kind__eq,
                TimestampGte: timestamp__gte,
                TimestampLt: timestamp__lt,
                Sort: sort,
                PageId: page_id,
                Limit: limit ?? 100), ct);
            return Results.Ok(new
            {
                items = page.Items.Select(e =>
                    JsonSerializer.SerializeToNode(e, typeof(EventBase), EventJson.Options)),
                next_page_id = page.NextPageId,
            });
        });

        group.MapGet("/{id:guid}/events/count", async (
            Guid id, string? kind__eq, DateTimeOffset? timestamp__gte,
            DateTimeOffset? timestamp__lt,
            IEventService events, CancellationToken ct) =>
            Results.Ok(await events.CountAsync(id, new EventQuery(
                KindEq: kind__eq,
                TimestampGte: timestamp__gte,
                TimestampLt: timestamp__lt), ct)));

        group.MapGet("/{id:guid}/events", async (
            Guid id, string ids, IEventService events, CancellationToken ct) =>
        {
            var parsed = ids.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var batch = await events.GetBatchAsync(id, parsed, ct);
            return Results.Ok(batch.Select(e =>
                JsonSerializer.SerializeToNode(e, typeof(EventBase), EventJson.Options)));
        });

        return app;
    }
}
