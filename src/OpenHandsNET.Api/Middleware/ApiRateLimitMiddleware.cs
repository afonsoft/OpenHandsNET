using System.Security.Claims;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Coordination;

namespace OpenHandsNET.Api.Middleware;

/// <summary>SPEC-36 RF-002: janela fixa global — <c>RateLimit:*</c>.</summary>
public sealed class ApiRateLimitOptions
{
    /// <summary>Seção de configuração.</summary>
    public const string Section = "RateLimit";

    /// <summary>0 = desligado (default); &gt;0 = limite global/min por identidade.</summary>
    public int RequestsPerMinute { get; set; }
}

/// <summary>
/// SPEC-36 RF-002/004: rate limit global via <see cref="IDistributedCounter"/>
/// — com Redis o limite vale p/ o cluster inteiro (não por réplica); com
/// <c>memory</c> vale por processo (single-réplica). Fail-open: counter
/// indisponível retorna 0 → request passa (Redis down ≠ app down).
/// Só conta <c>/api/v1/*</c> — hubs (WS long-lived), health e static
/// assets ficam fora da janela.
/// </summary>
public sealed class ApiRateLimitMiddleware(
    RequestDelegate next,
    IDistributedCounter counter,
    IOptions<ApiRateLimitOptions> options,
    ILogger<ApiRateLimitMiddleware> logger)
{
    /// <inheritdoc />
    public async Task InvokeAsync(HttpContext ctx)
    {
        var limit = options.Value.RequestsPerMinute;
        if (limit <= 0 || !IsApiPath(ctx.Request.Path))
        {
            await next(ctx);
            return;
        }

        var id = Identity(ctx);
        var window = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmm");
        var count = await counter.IncrementAsync(
            $"ratelimit:{id}:{window}",
            TimeSpan.FromSeconds(70), ctx.RequestAborted);
        if (count > limit)
        {
            logger.LogWarning("rate limit hit {Identity} ({Count}/{Limit})",
                id, count, limit);
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ctx.Response.Headers.RetryAfter = "60";
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    code = "rate_limited",
                    message = "Rate limit exceeded — retry later.",
                    limit,
                },
            }, ctx.RequestAborted);
            return;
        }

        await next(ctx);
    }

    private static bool IsApiPath(PathString path) =>
        path.StartsWithSegments("/api/v1")
        && !path.StartsWithSegments("/api/v1/health")
        && !path.StartsWithSegments("/api/v1/ready");

    private static string Identity(HttpContext ctx)
    {
        var user = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (user is not null)
        {
            return $"u:{user}";
        }

        var key = ctx.Request.Headers.Authorization.ToString();
        if (key.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            // Nunca loga a key — hash curto só para bucketing.
            return $"k:{Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(key)))[..16]}";
        }

        return $"ip:{ctx.Connection.RemoteIpAddress}";
    }
}
