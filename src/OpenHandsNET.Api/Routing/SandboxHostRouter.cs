using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Sandboxing;

namespace OpenHandsNET.Api.Routing;

/// <summary>
/// SPEC-38 RF-002/004: router de sandbox por <c>Host</c> (modo
/// <c>host</c>: <c>{id}-{port}.{PublicBaseDomain}</c>) ou por path
/// (modo <c>path</c>: <c>/sandbox/{id}/port/{port}/…</c>).
/// SSRF guard: só aceita <c>{guid:N}[-{port}]</c> válido e resolve o
/// upstream pelo record (nome do container na rede interna/control).
/// Auth: cookie/JMT da Api — 401 antes de proxear. WS pass-through
/// para o <c>/exec/stream</c> do sandbox-agent.
/// </summary>
public sealed class SandboxHostRouter(
    RequestDelegate next,
    IOptions<SandboxOptions> options)
{

    /// <summary>Middleware entrypoint.</summary>
    public async Task InvokeAsync(
        HttpContext ctx, ISandboxService sandboxes,
        SandboxSurfaceProxy proxy)
    {
        var mode = (options.Value.RoutingMode ?? "internal")
            .ToLowerInvariant();
        if (mode == "internal")
        {
            await next(ctx);
            return;
        }

        var target = mode == "host"
            ? MatchHost(ctx, options.Value.PublicBaseDomain)
            : MatchPath(ctx);
        if (target is null)
        {
            await next(ctx);
            return;
        }

        var (sandboxId, port) = target.Value;
        // RF-004/SPEC-40: auth de sessão ou preview token (?token=)
        // obrigatório antes de qualquer proxy — surfaces públicas
        // (ttyd/code-server) têm auth própria fraca ou nenhuma.
        if (ctx.User.Identity?.IsAuthenticated != true
            && !ctx.RequestServices
                .GetRequiredService<PreviewTokenService>()
                .Validate(ctx.Request.Query["token"], sandboxId, port))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var sandbox = await sandboxes.GetAsync(sandboxId,
            ctx.RequestAborted);
        if (sandbox?.ContainerId is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Upstream interno: nome do container resolve na rede interna
        // (compose/k8s DNS ou control network do SPEC-35).
        var upstream = SandboxSurfaceProxy.UpstreamFor(
            sandbox.ContainerId, port);
        var path = mode == "path"
            ? StripPathPrefix(ctx.Request.Path)
            : ctx.Request.Path.Value ?? "/";

        await proxy.ProxyAsync(ctx, upstream, path);
    }

    private static (Guid Id, int Port)? MatchHost(
        HttpContext ctx, string? domain)
    {
        if (domain is null || ctx.Request.Host.Host is not { } host
            || !host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var label = host[..^(domain.Length + 1)];
        var parts = label.Split('-');
        if (parts.Length is < 1 or > 2
            || !Guid.TryParseExact(parts[0], "N", out var id))
        {
            return null;
        }

        var port = parts.Length == 2 && int.TryParse(parts[1], out var p)
            ? p : 8000;
        return (id, port);
    }

    private static (Guid Id, int Port)? MatchPath(HttpContext ctx)
    {
        var seg = ctx.Request.Path.Value?.Split('/',
            StringSplitOptions.RemoveEmptyEntries) ?? [];
        // /sandbox/{id}/port/{port}/…
        if (seg.Length >= 4 && seg[0] == "sandbox" && seg[2] == "port"
            && Guid.TryParseExact(seg[1], "N", out var id)
            && int.TryParse(seg[3], out var port))
        {
            return (id, port);
        }

        return null;
    }

    private static string StripPathPrefix(string? path)
    {
        if (path is null)
        {
            return "/";
        }

        var idx = 0;
        for (var i = 0; i < 4; i++)
        {
            idx = path.IndexOf('/', idx + 1);
            if (idx < 0)
            {
                return "/";
            }
        }

        return path[idx..];
    }
}