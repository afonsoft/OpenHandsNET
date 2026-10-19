using System.Net.WebSockets;

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
    IHttpClientFactory httpFactory,
    IOptions<SandboxOptions> options)
{
    /// <summary>Hop-by-hop headers nunca propagados.</summary>
    private static readonly string[] Strip =
    [
        "Host", "Connection", "Keep-Alive", "Transfer-Encoding",
        "Upgrade", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer",
    ];

    /// <summary>Middleware entrypoint.</summary>
    public async Task InvokeAsync(
        HttpContext ctx, ISandboxService sandboxes)
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
        var upstream = new Uri(
            $"http://{sandbox.ContainerId[..12]}:{port}");
        var path = mode == "path"
            ? StripPathPrefix(ctx.Request.Path)
            : ctx.Request.Path.Value ?? "/";

        if (ctx.WebSockets.IsWebSocketRequest)
        {
            await ProxyWebSocketAsync(ctx, upstream, path);
            return;
        }

        await ProxyHttpAsync(ctx, upstream, path);
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

    private async Task ProxyHttpAsync(
        HttpContext ctx, Uri upstream, string path)
    {
        var req = new HttpRequestMessage(
            new HttpMethod(ctx.Request.Method),
            new Uri(upstream, path + ctx.Request.QueryString));
        foreach (var (k, v) in ctx.Request.Headers)
        {
            if (!Strip.Contains(k, StringComparer.OrdinalIgnoreCase))
            {
                req.Headers.TryAddWithoutValidation(k, v.ToArray());
            }
        }

        if (ctx.Request.ContentLength > 0)
        {
            req.Content = new StreamContent(ctx.Request.Body);
        }

        req.Headers.TryAddWithoutValidation("X-Forwarded-For",
            ctx.Connection.RemoteIpAddress?.ToString());
        req.Headers.TryAddWithoutValidation("X-Forwarded-Host",
            ctx.Request.Host.Value);
        req.Headers.TryAddWithoutValidation("X-Forwarded-Proto",
            ctx.Request.Scheme);

        using var res = await httpFactory.CreateClient("sandbox-router")
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead,
                ctx.RequestAborted);
        ctx.Response.StatusCode = (int)res.StatusCode;
        foreach (var (k, v) in res.Headers.Concat(res.Content.Headers))
        {
            ctx.Response.Headers[k] = v.ToArray();
        }

        await res.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    private static async Task ProxyWebSocketAsync(
        HttpContext ctx, Uri upstream, string path)
    {
        using var client = new ClientWebSocket();
        var wsUri = new UriBuilder(upstream)
        {
            Scheme = "ws",
            Path = path,
            Query = ctx.Request.QueryString.Value?.TrimStart('?'),
        }.Uri;
        await client.ConnectAsync(wsUri, ctx.RequestAborted);
        using var server = await ctx.WebSockets.AcceptWebSocketAsync();
        var pump = async (WebSocket from, WebSocket to) =>
        {
            var buf = new byte[64 * 1024];
            while (from.State == WebSocketState.Open)
            {
                var r = await from.ReceiveAsync(buf, ctx.RequestAborted);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await to.CloseAsync(WebSocketCloseStatus.NormalClosure,
                        null, CancellationToken.None);
                    return;
                }

                await to.SendAsync(buf.AsMemory(0, r.Count),
                    r.MessageType, r.EndOfMessage, CancellationToken.None);
            }
        };
        await Task.WhenAny(pump(server, client), pump(client, server));
    }
}