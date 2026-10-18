using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using OpenHandsNET.Application.Coordination;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Application.Sharing;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// SPEC-56 — public share links + trajectory download.
/// Authenticated side: create/revoke/status under
/// <c>/api/v1/app-conversations/{id}/share</c> and
/// <c>/download</c>. Public side: <c>/api/v1/shared/{token}</c> +
/// <c>/events</c> with per-IP rate limit (default 60/min).
/// </summary>
public static class ShareEndpoints
{
    /// <summary>Maps share endpoints.</summary>
    public static IEndpointRouteBuilder MapShareEndpoints(
        this IEndpointRouteBuilder app)
    {
        var conv = app.MapGroup("/api/v1/app-conversations")
            .WithTags("Sharing");

        conv.MapPost("/{id:guid}/share", async (
            Guid id, IShareService shares,
            ICurrentUserAccessor current, CancellationToken ct) =>
        {
            try
            {
                var result = await shares.CreateOrGetAsync(
                    id, current.RequireUserId(), ct);
                return Results.Ok(new
                {
                    url = result.Url,
                    token = result.Token is { Length: > 0 } t ? t : null,
                    expires_at = result.ExpiresAt,
                });
            }
            catch (ShareForbiddenException ex)
            {
                return Results.Json(new { error = ex.Message },
                    statusCode: 403);
            }
        }).RequireAuthorization();

        conv.MapGet("/{id:guid}/share", async (
            Guid id, IShareService shares,
            ICurrentUserAccessor current, CancellationToken ct) =>
        {
            try
            {
                var s = await shares.GetStatusAsync(
                    id, current.RequireUserId(), ct);
                return Results.Ok(new
                {
                    shared = s.Shared, url = s.Url, expires_at = s.ExpiresAt,
                });
            }
            catch (ShareForbiddenException ex)
            {
                return Results.Json(new { error = ex.Message },
                    statusCode: 403);
            }
        }).RequireAuthorization();

        conv.MapDelete("/{id:guid}/share", async (
            Guid id, IShareService shares,
            ICurrentUserAccessor current, CancellationToken ct) =>
        {
            try
            {
                return await shares.RevokeAsync(
                    id, current.RequireUserId(), ct)
                        ? Results.NoContent() : Results.NotFound();
            }
            catch (ShareForbiddenException ex)
            {
                return Results.Json(new { error = ex.Message },
                    statusCode: 403);
            }
        }).RequireAuthorization();

        // Trajectory download — streamed zip (SPEC-56 RF-004).
        conv.MapGet("/{id:guid}/download", async (
            Guid id, HttpContext http,
            ITrajectoryExporter exporter,
            ICurrentUserAccessor current, CancellationToken ct) =>
        {
            var slug = $"openhandsnet-{id:N}".ToLowerInvariant();
            http.Response.ContentType = "application/zip";
            http.Response.Headers.ContentDisposition =
                $"attachment; filename=\"{slug}.zip\"";
            try
            {
                await exporter.WriteZipAsync(
                    id, current.RequireUserId(), http.Response.Body, ct);
            }
            catch (ShareForbiddenException)
            {
                if (!http.Response.HasStarted)
                {
                    http.Response.StatusCode = 403;
                }
            }
        }).RequireAuthorization();

        // Public side — token IS the credential (SPEC-56 RF-001).
        var shared = app.MapGroup("/api/v1/shared").WithTags("Sharing");

        shared.MapGet("/{token}", async (
            string token, HttpContext http,
            IShareService shares, IDistributedCounter counter,
            IConfiguration config, CancellationToken ct) =>
        {
            if (await IsRateLimitedAsync(http, counter, config, ct))
            {
                return Results.StatusCode(429);
            }

            var payload = await shares.GetSharedAsync(token, 100, ct);
            return payload is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    title = payload.Title,
                    selected_repository = payload.SelectedRepository,
                    agent_type = payload.AgentType,
                    execution_status = payload.ExecutionStatus,
                    created_at = payload.CreatedAt,
                    items = payload.Items,
                    next_page_id = payload.NextPageId,
                });
        });

        shared.MapGet("/{token}/events", async (
            string token, HttpContext http,
            [FromQuery(Name = "page_id")] string? pageId,
            [FromQuery] int limit,
            IShareService shares, IDistributedCounter counter,
            IConfiguration config, CancellationToken ct) =>
        {
            if (await IsRateLimitedAsync(http, counter, config, ct))
            {
                return Results.StatusCode(429);
            }

            var page = await shares.GetSharedEventsAsync(
                token, pageId, limit <= 0 ? 100 : limit, ct);
            return page is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    items = page.Items,
                    next_page_id = page.NextPageId,
                });
        });

        return app;
    }

    /// <summary>
    /// Per-IP fixed-window limit for the public share surface
    /// (<c>Shares:PublicRateLimitPerMinute</c>, default 60/min).
    /// </summary>
    private static async Task<bool> IsRateLimitedAsync(
        HttpContext http, IDistributedCounter counter,
        IConfiguration config, CancellationToken ct)
    {
        var limit = int.TryParse(
            config["Shares:PublicRateLimitPerMinute"], out var v)
                ? v : 60;
        if (limit <= 0)
        {
            return false;
        }

        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var window = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmm");
        var count = await counter.IncrementAsync(
            $"share:{ip}:{window}", TimeSpan.FromSeconds(70), ct);
        return count > limit;
    }
}
