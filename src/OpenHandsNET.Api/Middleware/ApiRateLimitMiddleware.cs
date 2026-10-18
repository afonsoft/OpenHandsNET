using System.Security.Claims;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Coordination;

namespace OpenHandsNET.Api.Middleware;

/// <summary>
/// Rate limits por classe de endpoint (SPEC-36 → SPEC-59 RF-003).
/// Qualquer bucket 0 = desligado; <see cref="RequestsPerMinute"/>
/// (legado) é o default quando o bucket específico é 0.
/// </summary>
public sealed class ApiRateLimitOptions
{
    /// <summary>Seção de configuração.</summary>
    public const string Section = "RateLimit";

    /// <summary>Default/min por ator (0 = desligado, default).</summary>
    public int RequestsPerMinute { get; set; }

    /// <summary>GET/HEAD.</summary>
    public int ReadPerMinute { get; set; }

    /// <summary>POST/PATCH/PUT/DELETE genérico.</summary>
    public int WritePerMinute { get; set; }

    /// <summary>Webhooks inbound (chat/scm/tickets).</summary>
    public int WebhookPerMinute { get; set; }

    /// <summary>Creates/send-message/stream-start (caros).</summary>
    public int ExpensivePerMinute { get; set; }
}

/// <summary>
/// SPEC-59 RF-003: janela fixa por ator + bucket — chave
/// <c>{actor_kind}:{actor_id}:{bucket}:{window}</c> via
/// <see cref="IDistributedCounter"/> (multi-réplica com Redis;
/// fail-open quando indisponível). Buckets: read / write / webhook /
/// expensive. Resposta 429 + <c>Retry-After</c> + headers
/// <c>X-RateLimit-*</c>.
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
        var o = options.Value;
        if (!IsApiPath(ctx.Request.Path))
        {
            await next(ctx);
            return;
        }

        var bucket = BucketOf(ctx.Request);
        var limit = bucket switch
        {
            "webhook" => First(o.WebhookPerMinute, o.RequestsPerMinute),
            "expensive" => First(o.ExpensivePerMinute, o.WritePerMinute,
                o.RequestsPerMinute),
            "write" => First(o.WritePerMinute, o.RequestsPerMinute),
            _ => First(o.ReadPerMinute, o.RequestsPerMinute),
        };
        if (limit <= 0)
        {
            await next(ctx);
            return;
        }

        var id = Identity(ctx);
        var window = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmm");
        var count = await counter.IncrementAsync(
            $"ratelimit:{id}:{bucket}:{window}",
            TimeSpan.FromSeconds(70), ctx.RequestAborted);
        var reset = 60 - DateTime.UtcNow.Second;
        ctx.Response.Headers["X-RateLimit-Limit"] = limit.ToString();
        ctx.Response.Headers["X-RateLimit-Remaining"] =
            Math.Max(0, limit - count).ToString();
        ctx.Response.Headers["X-RateLimit-Reset"] = reset.ToString();
        if (count > limit)
        {
            logger.LogWarning(
                "rate limit hit {Identity} bucket={Bucket} ({Count}/{Limit})",
                id, bucket, count, limit);
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ctx.Response.Headers.RetryAfter = reset.ToString();
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    code = "rate_limited",
                    message = "Rate limit exceeded — retry later.",
                    limit, bucket,
                },
            }, ctx.RequestAborted);
            return;
        }

        await next(ctx);
    }

    private static int First(params int[] candidates) =>
        candidates.FirstOrDefault(c => c > 0);

    /// <summary>Bucket por classe de endpoint.</summary>
    internal static string BucketOf(HttpRequest req)
    {
        var p = req.Path.Value ?? "";
        if (p.Contains("/webhooks/", StringComparison.Ordinal)
            || p.Contains("/chatops/", StringComparison.Ordinal)
            || p.Contains("/scm/events", StringComparison.Ordinal)
            || p.Contains("/tickets/inbound", StringComparison.Ordinal))
        {
            return "webhook";
        }
        var expensive = HttpMethods.IsPost(req.Method)
            && (p.EndsWith("/app-conversations", StringComparison.Ordinal)
                || p.Contains("/send-message", StringComparison.Ordinal)
                || p.Contains("/stream-start", StringComparison.Ordinal));
        if (expensive)
            return "expensive";
        return HttpMethods.IsGet(req.Method) || HttpMethods.IsHead(req.Method)
            ? "read" : "write";
    }

    private static bool IsApiPath(PathString path) =>
        path.StartsWithSegments("/api/v1")
        && !path.StartsWithSegments("/api/v1/health")
        && !path.StartsWithSegments("/api/v1/ready");

    /// <summary><c>{kind}:{id}</c> — service_user/user (claims) →
    /// api-key hash → ip-anon.</summary>
    private static string Identity(HttpContext ctx)
    {
        var kind = ctx.User.FindFirst("actor_kind")?.Value;
        var id = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (id is not null)
        {
            return $"{kind ?? "user"}:{id}";
        }

        var key = ctx.Request.Headers.Authorization.ToString();
        if (key.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            // Nunca loga a key — hash curto só para bucketing.
            return $"key:{Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(key)))[..16]}";
        }

        return $"ip:{ctx.Connection.RemoteIpAddress}";
    }
}
