using System.Net.WebSockets;

namespace OpenHandsNET.Api.Routing;

/// <summary>
/// SPEC-113 RF-003: proxy HTTP/WebSocket para uma porta do sandbox —
/// extraído do <see cref="SandboxHostRouter"/> para ser reusado pelo
/// endpoint same-origin <c>/api/v1/sandboxes/{id}/surfaces/{port}/…</c>,
/// que serve surfaces em qualquer <c>RoutingMode</c> (inclusive
/// <c>internal</c>, onde o middleware é no-op).
/// Upstream interno: nome do container resolve na rede interna — mesma
/// confiança do router (NOSONAR S5332).
/// </summary>
public sealed class SandboxSurfaceProxy(IHttpClientFactory httpFactory)
{
    /// <summary>Hop-by-hop headers nunca propagados.</summary>
    private static readonly string[] Strip =
    [
        "Host", "Connection", "Keep-Alive", "Transfer-Encoding",
        "Upgrade", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer",
    ];

    /// <summary>Resolve o upstream pelo record (nome/id do container).</summary>
    public static Uri UpstreamFor(string containerId, int port) =>
        new($"http://{containerId[..Math.Min(12, containerId.Length)]}:{port}");

    /// <summary>Proxies a request (HTTP ou upgrade WebSocket).</summary>
    public async Task ProxyAsync(HttpContext ctx, Uri upstream, string path)
    {
        if (ctx.WebSockets.IsWebSocketRequest)
        {
            await ProxyWebSocketAsync(ctx, upstream, path);
            return;
        }

        await ProxyHttpAsync(ctx, upstream, path);
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