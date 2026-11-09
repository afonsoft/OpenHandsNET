using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

using Sandcastle.Application.Coordination;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Sharing;

namespace Sandcastle.Api.Endpoints;

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

        conv.MapPost("/{id:guid}/share",
            CreateShareAsync).RequireAuthorization();

        conv.MapGet("/{id:guid}/share",
            GetShareAsync).RequireAuthorization();

        conv.MapDelete("/{id:guid}/share",
            RevokeShareAsync).RequireAuthorization();

        // Trajectory download — streamed zip (SPEC-56 RF-004).
        conv.MapGet("/{id:guid}/download",
            DownloadTrajectoryAsync).RequireAuthorization();

        // Public side — token IS the credential (SPEC-56 RF-001).
        // SPEC-80: AllowAnonymous é metadata-only aqui (a rota já era
        // pública); declara `security: []` no OpenAPI.
        var shared = app.MapGroup("/api/v1/shared").WithTags("Sharing")
            .AllowAnonymous();

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
            [AsParameters] SharedEventsParams p) =>
        {
            if (await IsRateLimitedAsync(
                p.Http, p.Counter, p.Config, p.Ct))
            {
                return Results.StatusCode(429);
            }

            var page = await p.Shares.GetSharedEventsAsync(
                p.Token, p.PageId, p.Limit <= 0 ? 100 : p.Limit, p.Ct);
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


    private static async Task<IResult> CreateShareAsync(
        Guid id, IShareService shares,
        ICurrentUserAccessor current, CancellationToken ct)
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
    }

    private static async Task<IResult> GetShareAsync(
        Guid id, IShareService shares,
        ICurrentUserAccessor current, CancellationToken ct)
    {
        try
        {
            var s = await shares.GetStatusAsync(
                id, current.RequireUserId(), ct);
            return Results.Ok(new
            {
                shared = s.Shared,
                url = s.Url,
                expires_at = s.ExpiresAt,
            });
        }
        catch (ShareForbiddenException ex)
        {
            return Results.Json(new { error = ex.Message },
                statusCode: 403);
        }
    }

    private static async Task<IResult> RevokeShareAsync(
        Guid id, IShareService shares,
        ICurrentUserAccessor current, CancellationToken ct)
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
    }

    private static async Task<IResult> DownloadTrajectoryAsync(
        Guid id, HttpContext http,
        ITrajectoryExporter exporter,
        ICurrentUserAccessor current, CancellationToken ct)
    {
        var slug = $"sandcastle-{id:N}".ToLowerInvariant();
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

        return Results.Empty;
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

    /// <summary>Binding do feed público de eventos (SPEC-104 — AsParameters).</summary>
    private sealed class SharedEventsParams
    {
        [FromRoute] public string Token { get; init; } = null!;
        public HttpContext Http { get; init; } = null!;
        [FromQuery(Name = "page_id")] public string? PageId { get; init; }
        [FromQuery(Name = "limit")] public int Limit { get; init; }
        [FromServices] public IShareService Shares { get; init; } = null!;
        [FromServices] public IDistributedCounter Counter { get; init; } = null!;
        [FromServices] public IConfiguration Config { get; init; } = null!;
        public CancellationToken Ct { get; init; }
    }
}