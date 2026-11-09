using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Events;
using Sandcastle.Domain.Events;

namespace Sandcastle.Api.Endpoints;

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
            [AsParameters] EventSearchParams p) =>
        {
            var sort = p.SortOrder?.Equals("TIMESTAMP_DESC",
                StringComparison.OrdinalIgnoreCase) == true
                ? EventSortOrder.TimestampDesc : EventSortOrder.Timestamp;
            var page = await p.Events.SearchAsync(p.Id, new EventQuery(
                KindEq: p.KindEq,
                TimestampGte: p.TimestampGte,
                TimestampLt: p.TimestampLt,
                Sort: sort,
                PageId: p.PageId,
                Limit: p.Limit ?? 100), p.Ct);
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

    /// <summary>Parâmetros de binding do search (SPEC-104 — AsParameters).</summary>
    private sealed class EventSearchParams
    {
        [FromRoute] public Guid Id { get; init; }
        [FromQuery(Name = "kind__eq")] public string? KindEq { get; init; }
        [FromQuery(Name = "timestamp__gte")] public DateTimeOffset? TimestampGte { get; init; }
        [FromQuery(Name = "timestamp__lt")] public DateTimeOffset? TimestampLt { get; init; }
        [FromQuery(Name = "sort_order")] public string? SortOrder { get; init; }
        [FromQuery(Name = "page_id")] public string? PageId { get; init; }
        [FromQuery(Name = "limit")] public int? Limit { get; init; }
        [FromServices] public IEventService Events { get; init; } = null!;
        public CancellationToken Ct { get; init; }
    }
}