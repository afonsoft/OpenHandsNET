using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sandcastle.Mcp;

/// <summary>
/// SPEC-145 RF-004 — transporte Streamable HTTP opcional do MCP
/// (<c>--http :PORT</c>, desligado por default — stdio continua o
/// transporte padrão). Um endpoint <c>POST /mcp</c> recebe mensagens
/// JSON-RPC 2.0 (single ou batch) e devolve <c>application/json</c>.
///
/// Segurança: exige <c>Authorization: Bearer sk-*</c> por request (o
/// token é repassado à API — multi-tenant por chamada, como no edge do
/// app) e honra a IP allowlist (<c>SC_MCP_IP_ALLOWLIST</c> — CIDRs
/// separados por vírgula, mesma semântica do SPEC-59; vazio = só
/// loopback). Sem GET/SSE nesta versão.
/// </summary>
public sealed class McpHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly McpServer _server = new();
    private readonly string _baseUrl;
    private readonly IReadOnlyList<string> _allowlist;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Cria o listener no prefixo <c>http://*:{port}/</c>.</summary>
    public McpHttpServer(int port, string baseUrl, string? allowlistCsv)
    {
        _baseUrl = baseUrl;
        _allowlist = ParseAllowlist(allowlistCsv);
        _listener.Prefixes.Add($"http://*:{port}/");
    }

    /// <summary>Roda o loop de requests até <paramref name="ct"/> cancelar.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Start();
        using var reg = ct.Register(() => _stop.Cancel());
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext? ctx;
            try
            {
                ctx = await _listener.GetContextAsync()
                    .WaitAsync(_stop.Token);
            }
            catch (Exception) when (ct.IsCancellationRequested
                || _stop.IsCancellationRequested)
            {
                break;
            }
            _ = Task.Run(() => HandleAsync(ctx, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx,
        CancellationToken ct)
    {
        try
        {
            var res = ctx.Response;
            var req = ctx.Request;

            // IP allowlist (SPEC-59 semantics: vazio = loopback-only).
            var ip = req.RemoteEndPoint?.Address;
            if (ip is null || !_allowlist.Any(c => MatchesCidr(c, ip)))
            {
                await WriteJsonAsync(res, 403,
                    """{"error":"ip_blocked"}""", ct);
                return;
            }

            if (!req.Url!.AbsolutePath.Equals("/mcp",
                    StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(res, 404,
                    """{"error":"not_found"}""", ct);
                return;
            }

            // Bearer sk-* (SPEC-145 security — sem credencial = 401).
            var auth = req.Headers["Authorization"];
            var token = auth is not null
                && auth.StartsWith("Bearer ", StringComparison.Ordinal)
                    ? auth["Bearer ".Length..].Trim() : null;
            if (token is null || !token.StartsWith("sk-",
                    StringComparison.Ordinal))
            {
                res.Headers["WWW-Authenticate"] = "Bearer";
                await WriteJsonAsync(res, 401,
                    """{"error":"missing_bearer_token"}""", ct);
                return;
            }

            if (!req.HttpMethod.Equals("POST",
                    StringComparison.OrdinalIgnoreCase))
            {
                res.Headers["Allow"] = "POST";
                await WriteJsonAsync(res, 405,
                    """{"error":"method_not_allowed"}""", ct);
                return;
            }

            string body;
            using (var reader = new StreamReader(
                req.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync(ct);
            }
            if (string.IsNullOrWhiteSpace(body))
            {
                await WriteJsonAsync(res, 400,
                    """{"error":"empty_body"}""", ct);
                return;
            }

            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(60),
            };
            var api = new ScApiClient(http, _baseUrl, token);

            var responses = new List<string>();
            foreach (var line in SplitRequests(body))
            {
                var r = await _server.HandleLineAsync(line, api, ct);
                if (r is not null)
                {
                    responses.Add(r);
                }
            }

            if (responses.Count == 0)
            {
                res.StatusCode = 202; // notifications-only batch
                res.Close();
                return;
            }
            var payload = responses.Count == 1
                ? responses[0]
                : "[" + string.Join(",", responses) + "]";
            await WriteJsonAsync(res, 200, payload, ct);
        }
        catch (Exception)
        {
            try
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
            }
            catch (Exception)
            {
                // socket já fechado — nada a fazer.
            }
        }
    }

    /// <summary>Single request ou JSON-RPC batch array.</summary>
    private static IEnumerable<string> SplitRequests(string body)
    {
        var trimmed = body.TrimStart();
        if (!trimmed.StartsWith('['))
        {
            yield return body;
            yield break;
        }
        JsonArray? batch = null;
        try
        {
            batch = JsonNode.Parse(body) as JsonArray;
        }
        catch (JsonException)
        {
            // cai fora — devolve o body cru p/ parse error do handler.
        }
        if (batch is null || batch.Count == 0)
        {
            yield return body;
            yield break;
        }
        foreach (var node in batch)
        {
            if (node is not null)
            {
                yield return node.ToJsonString();
            }
        }
    }

    /// <summary>CSV de CIDRs — null/vazio = loopback only.</summary>
    internal static IReadOnlyList<string> ParseAllowlist(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? ["127.0.0.1/8", "::1/128"]
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);

    /// <summary>CIDR match — IPv4/IPv6, <c>10.0.0.0/8</c> ou IP simples
    /// (mesma semântica do EdgeAccessMiddleware/SPEC-59).</summary>
    internal static bool MatchesCidr(string cidr, IPAddress ip)
    {
        var parts = cidr.Split('/');
        if (!IPAddress.TryParse(parts[0], out var network)
            || network.AddressFamily != ip.AddressFamily)
        {
            return false;
        }
        if (parts.Length == 1)
        {
            return network.Equals(ip);
        }
        if (!int.TryParse(parts[1], out var bits)
            || bits < 0)
        {
            return false;
        }
        var nBytes = network.GetAddressBytes();
        var iBytes = ip.GetAddressBytes();
        var fullBytes = bits / 8;
        var rem = bits % 8;
        if (bits > nBytes.Length * 8)
        {
            return false;
        }
        for (var i = 0; i < fullBytes; i++)
        {
            if (nBytes[i] != iBytes[i])
            {
                return false;
            }
        }
        if (rem > 0)
        {
            var mask = (byte)(0xFF << (8 - rem));
            if ((nBytes[fullBytes] & mask) != (iBytes[fullBytes] & mask))
            {
                return false;
            }
        }
        return true;
    }

    private static async Task WriteJsonAsync(HttpListenerResponse res,
        int status, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        res.StatusCode = status;
        res.ContentType = "application/json";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes, ct);
        res.Close();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
    }
}