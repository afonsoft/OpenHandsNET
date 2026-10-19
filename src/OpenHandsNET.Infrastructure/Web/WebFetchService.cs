using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Web;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Sandbox;
using OpenHandsNET.Sandboxing;

namespace OpenHandsNET.Infrastructure.Web;

/// <summary>
/// <see cref="IWebFetchService"/> — http(s) only, SSRF guard (DNS →
/// rejeita IPs privados/loopback/link-local), respeita a allowlist de
/// egress quando <c>EgressMode=allowlist</c>, strip markdown-lite,
/// saída passa pelo <c>SecretRedactor</c>.
/// </summary>
public sealed partial class WebFetchService(
    IHttpClientFactory httpFactory,
    IOptions<SandboxOptions> sandboxOptions,
    ILogger<WebFetchService> logger) : IWebFetchService
{
    /// <inheritdoc />
    public async Task<WebFetchResult> FetchAsync(
        string url, int maxChars, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return new WebFetchResult(
                false, url, 0, null, "only http(s) URLs are allowed");
        }

        if (await IsBlockedHostAsync(uri, ct))
        {
            return new WebFetchResult(
                false, url, 0, null, "blocked by SSRF guard");
        }

        var opts = sandboxOptions.Value;
        if (opts.EgressMode.Equals("allowlist", StringComparison.OrdinalIgnoreCase)
            && !IsAllowed(uri.Host, opts.EgressAllow))
        {
            return new WebFetchResult(
                false, url, 0, null,
                $"host '{uri.Host}' not in the egress allowlist");
        }

        try
        {
            var http = httpFactory.CreateClient("web-fetch");
            using var res = await http.GetAsync(uri, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            var text = StripHtml(body);
            if (text.Length > maxChars)
            {
                text = text[..maxChars] + "\n[…truncated…]";
            }

            return new WebFetchResult(true, res.RequestMessage?.RequestUri
                ?.ToString() ?? url, (int)res.StatusCode,
                SecretRedactor.Redact(text), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "web_fetch falhou para {Url}", url);
            return new WebFetchResult(
                false, url, 0, null, ex.Message);
        }
    }

    /// <summary>host ∈ allowlist — match exato ou sufixo <c>.host</c>.</summary>
    internal static bool IsAllowed(string host, string? allow)
    {
        if (string.IsNullOrWhiteSpace(allow))
        {
            return false;
        }

        foreach (var raw in allow.Split(';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pat = raw.TrimStart('*').TrimStart('.');
            if (host.Equals(pat, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + pat, StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith('*')
                    && host.EndsWith(pat, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>SSRF: resolve o host e rejeita endereços privados.</summary>
    private static async Task<bool> IsBlockedHostAsync(
        Uri uri, CancellationToken ct)
    {
        if (uri.HostNameType is UriHostNameType.IPv4
            or UriHostNameType.IPv6)
        {
            return IsPrivate(IPAddress.Parse(uri.Host));
        }

        IPAddress[] addrs;
        try
        {
            addrs = await Dns.GetHostAddressesAsync(uri.Host, ct);
        }
        catch (Exception)
        {
            return true; // DNS failure — trate como bloqueado (fail-closed).
        }

        return addrs.Length == 0 || addrs.Any(IsPrivate);
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal
            || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] is 10 or 127 or 0
                || b[0] == 169 && b[1] == 254          // link-local
                || b[0] == 172 && b[1] is >= 16 and <= 31
                || b[0] == 192 && b[1] == 168
                || b[0] == 100 && b[1] is >= 64 and <= 127; // CGNAT
        }

        // IPv6: fc00::/7 (ULA), fe80::/10 já coberto acima, ::1 loopback.
        return ip.IsIPv6SiteLocal || b[0] is 0xFC or 0xFD;
    }

    /// <summary>markdown-lite: remove tags/scripts, colapsa whitespace.</summary>
    internal static string StripHtml(string html)
    {
        var s = ScriptRegex().Replace(html, " ");
        s = TagRegex().Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        s = BlankLineRegex().Replace(s, "\n");
        s = SpaceRegex().Replace(s, " ");
        return s.Trim();
    }

    [GeneratedRegex("<(script|style)[^>]*>.*?</\\1>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\s*\\n\\s*")]
    private static partial Regex BlankLineRegex();

    [GeneratedRegex("[ \\t]{2,}")]
    private static partial Regex SpaceRegex();
}