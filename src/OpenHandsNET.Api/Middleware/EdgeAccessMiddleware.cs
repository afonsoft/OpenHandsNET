using System.Net;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Api.Middleware;

/// <summary>Config do edge middleware.</summary>
public sealed class EdgeAccessOptions
{
    /// <summary>Seção <c>Edge</c>.</summary>
    public const string Section = "Edge";

    /// <summary>Paths isentos da checagem (webhooks assinados, health).</summary>
    public string[] ExemptPaths { get; set; } =
        ["/api/v1/health", "/api/v1/ready", "/scim/v2"];
}

/// <summary>
/// SPEC-59 RF-001: IP access lists — 1º middleware do pipeline (antes do
/// auth). Regras <c>ip_access_rules</c> por escopo (<c>global</c> ou
/// <c>org:{id}</c>); deny vence allow; se existir regra allow no escopo
/// do request, o IP precisa casar; senão, default allow. Regras em cache
/// 30s (<see cref="IMemoryCache"/> — multi-réplica fica at-most-30s
/// stale, documentado).
/// </summary>
public sealed class EdgeAccessMiddleware(
    RequestDelegate next,
    IMemoryCache cache,
    IServiceScopeFactory scopeFactory,
    IOptions<EdgeAccessOptions> options,
    ILogger<EdgeAccessMiddleware> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        if (IsExempt(path))
        {
            await next(ctx);
            return;
        }

        var ip = ResolveIp(ctx);
        if (ip is null)
        {
            await next(ctx);
            return;
        }

        var rules = await GetRulesAsync(ctx.RequestAborted);
        var scopeKey = ScopeOf(path);
        var applicable = rules
            .Where(r => r.Scope == "global" || r.Scope == scopeKey)
            .OrderByDescending(r => r.Priority)
            .ToList();

        var denies = applicable.Where(r => r.Action == "deny").ToList();
        var allows = applicable.Where(r => r.Action == "allow").ToList();

        var blocked = denies.Any(r => Matches(r.Cidr, ip));
        if (!blocked && allows.Any())
        {
            blocked = !allows.Any(r => Matches(r.Cidr, ip));
        }

        if (blocked)
        {
            logger.LogWarning("edge.ip_blocked {Ip} {Path}", ip, path);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(
                new { error = "ip_blocked" }, ctx.RequestAborted);
            return;
        }

        await next(ctx);
    }

    private bool IsExempt(PathString path)
    {
        foreach (var p in options.Value.ExemptPaths)
        {
            if (path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private async Task<List<(string Scope, string Cidr, string Action, int Priority)>>
        GetRulesAsync(CancellationToken ct)
    {
        if (cache.TryGetValue("edge.ip_rules", out List<(string, string, string, int)>? hit)
            && hit is not null)
        {
            return hit;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider
            .GetRequiredService<AppDbContext>();
        var rows = await db.IpAccessRules
            .AsNoTracking()
            .Select(r => new { r.Scope, r.Cidr, r.Action, r.Priority })
            .ToListAsync(ct);
        var list = rows
            .Select(r => (r.Scope, r.Cidr, r.Action, r.Priority))
            .ToList();
        cache.Set("edge.ip_rules", list, CacheTtl);
        return list;
    }

    /// <summary>Escopo org derivado do path (<c>/api/v1/orgs/{id}/…</c>).</summary>
    private static string ScopeOf(PathString path)
    {
        var seg = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (seg is { Length: > 2 }
            && seg[0] == "api" && seg[1] == "v1" && seg[2] == "orgs"
            && seg.Length > 3)
        {
            return $"org:{seg[3]}";
        }
        return "global";
    }

    private static IPAddress? ResolveIp(HttpContext ctx)
    {
        var trust = ctx.RequestServices
            .GetService<IConfiguration>()?
            .GetValue("Proxy:TrustForwardedHeaders", false) ?? false;
        if (trust
            && ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var fwd)
            && IPAddress.TryParse(fwd.ToString().Split(',')[0].Trim(), out var fip))
        {
            return fip;
        }
        return ctx.Connection.RemoteIpAddress;
    }

    /// <summary>CIDR match — IPv4/IPv6, <c>10.0.0.0/8</c> ou IP simples.</summary>
    internal static bool Matches(string cidr, IPAddress ip)
    {
        var parts = cidr.Split('/');
        if (!IPAddress.TryParse(parts[0], out var network))
            return false;
        if (network.AddressFamily != ip.AddressFamily)
            return false;
        if (parts.Length == 1)
            return network.Equals(ip);
        if (!int.TryParse(parts[1], out var bits))
            return false;

        var nBytes = network.GetAddressBytes();
        var iBytes = ip.GetAddressBytes();
        var fullBytes = bits / 8;
        var rem = bits % 8;
        if (bits < 0 || bits > nBytes.Length * 8)
            return false;
        for (var i = 0; i < fullBytes; i++)
        {
            if (nBytes[i] != iBytes[i])
                return false;
        }
        if (rem > 0)
        {
            var mask = (byte)(0xFF << (8 - rem));
            if ((nBytes[fullBytes] & mask) != (iBytes[fullBytes] & mask))
                return false;
        }
        return true;
    }
}