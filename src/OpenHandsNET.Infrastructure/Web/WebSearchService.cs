using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Web;

namespace OpenHandsNET.Infrastructure.Web;

/// <summary>
/// SPEC-133 RF-002/RF-003/RF-005: <see cref="IWebSearchService"/> —
/// provider dispatch por <c>WebSearch:Provider</c>
/// (duckduckgo|tavily|brave|none), cache/single-flight por processo,
/// resultados truncados (~300 chars) — conteúdo não-confiável.
/// </summary>
public sealed partial class WebSearchService(
    IHttpClientFactory httpFactory,
    IOptions<WebSearchOptions> options,
    ILogger<WebSearchService> logger) : IWebSearchService
{
    private const int MaxSnippetChars = 300;

    private static readonly ConcurrentDictionary<string, (DateTimeOffset Expires, Lazy<Task<WebSearchOutcome>> Lazy)> Cache = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<WebSearchOutcome> SearchAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(new WebSearchOutcome(
                false, [], "query must not be empty"));
        }

        if (opts.Provider.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new WebSearchOutcome(
                false, [], "web_search disabled (WebSearch:Provider=none)"));
        }

        var clamped = Math.Clamp(maxResults <= 0 ? 5 : maxResults, 1, 10);
        var key = $"{opts.Provider.ToLowerInvariant()}|{query.Trim().ToLowerInvariant()}|{clamped}";
        var now = DateTimeOffset.UtcNow;
        if (Cache.TryGetValue(key, out var hit) && hit.Expires > now)
        {
            return hit.Lazy.Value;
        }

        var entry = Cache[key] = (now.AddSeconds(opts.CacheSeconds),
            new Lazy<Task<WebSearchOutcome>>(() => RunAsync(
                opts.Provider, opts, query, clamped, logger, httpFactory)));
        return entry.Lazy.Value;
    }

    private static async Task<WebSearchOutcome> RunAsync(
        string provider, WebSearchOptions opts, string query,
        int maxResults, ILogger logger, IHttpClientFactory httpFactory)
    {
        try
        {
            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Max(2, opts.TimeoutSeconds)));
            var http = httpFactory.CreateClient("web-search");
            return provider.ToLowerInvariant() switch
            {
                "tavily" when !string.IsNullOrWhiteSpace(opts.ApiKey)
                    => await TavilyAsync(http, opts.ApiKey, query, maxResults, cts.Token),
                "brave" when !string.IsNullOrWhiteSpace(opts.ApiKey)
                    => await BraveAsync(http, opts.ApiKey, query, maxResults, cts.Token),
                "tavily" or "brave" => await DuckDuckGoAsync(http, query, maxResults, cts.Token),
                _ => await DuckDuckGoAsync(http, query, maxResults, cts.Token),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "web_search provider {Provider} failed", provider);
            return new WebSearchOutcome(false, [], $"web_search provider error: {ex.Message}");
        }
    }

    /// <summary>DDG html endpoint — zero config (SPEC-133 RF-002).</summary>
    private static async Task<WebSearchOutcome> DuckDuckGoAsync(
        HttpClient http, string query, int maxResults, CancellationToken ct)
    {
        var url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
        var html = await http.GetStringAsync(url, ct);
        var results = ParseDuckDuckGo(html, maxResults);
        return new WebSearchOutcome(true, results, null);
    }

    /// <summary>Parser resiliente do html.duckduckgo.com (SPEC-133 RF-002).</summary>
    internal static List<WebSearchResult> ParseDuckDuckGo(string html, int maxResults)
    {
        var results = new List<WebSearchResult>(maxResults);
        var links = DdgResultLink().Matches(html);
        var snippets = DdgSnippet().Matches(html);
        for (var i = 0; i < links.Count && results.Count < maxResults; i++)
        {
            var m = links[i];
            var href = WebUtility.HtmlDecode(m.Groups[1].Value);
            var uddg = DdgUddg().Match(href);
            var finalUrl = uddg.Success
                ? Uri.UnescapeDataString(uddg.Groups[1].Value)
                : href;
            var title = StripTags(WebUtility.HtmlDecode(m.Groups[2].Value));
            var snippet = i < snippets.Count
                ? StripTags(WebUtility.HtmlDecode(snippets[i].Groups[1].Value))
                : string.Empty;
            if (title.Length > 0 && finalUrl.StartsWith("http", StringComparison.Ordinal))
            {
                results.Add(new WebSearchResult(
                    title, finalUrl, Truncate(snippet, MaxSnippetChars)));
            }
        }

        return results;
    }

    private static async Task<WebSearchOutcome> TavilyAsync(
        HttpClient http, string apiKey, string query,
        int maxResults, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync(
            "https://api.tavily.com/search",
            new { api_key = apiKey, query, max_results = maxResults }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return new WebSearchOutcome(
                false, [], $"tavily HTTP {(int)resp.StatusCode}");
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var results = new List<WebSearchResult>(maxResults);
        if (doc.TryGetProperty("results", out var arr))
        {
            foreach (var r in arr.EnumerateArray())
            {
                results.Add(new WebSearchResult(
                    r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    r.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                    Truncate(r.TryGetProperty("content", out var c)
                        ? c.GetString() ?? "" : "", MaxSnippetChars)));
                if (results.Count >= maxResults)
                {
                    break;
                }
            }
        }

        return new WebSearchOutcome(true, results, null);
    }

    private static async Task<WebSearchOutcome> BraveAsync(
        HttpClient http, string apiKey, string query,
        int maxResults, CancellationToken ct)
    {
        var url = "https://api.search.brave.com/res/v1/web/search?q="
            + Uri.EscapeDataString(query) + "&count=" + maxResults;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Subscription-Token", apiKey);
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return new WebSearchOutcome(
                false, [], $"brave HTTP {(int)resp.StatusCode}");
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var results = new List<WebSearchResult>(maxResults);
        if (doc.TryGetProperty("web", out var web)
            && web.TryGetProperty("results", out var arr))
        {
            foreach (var r in arr.EnumerateArray())
            {
                results.Add(new WebSearchResult(
                    r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    r.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                    Truncate(r.TryGetProperty("description", out var d)
                        ? d.GetString() ?? "" : "", MaxSnippetChars)));
                if (results.Count >= maxResults)
                {
                    break;
                }
            }
        }

        return new WebSearchOutcome(true, results, null);
    }

    private static string StripTags(string s)
        => TagRe().Replace(s, string.Empty).Trim();

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    [GeneratedRegex("<[^>]+>")] private static partial Regex TagRe();
    [GeneratedRegex(@"<a[^>]+class=""[^""]*result__a[^""]*""[^>]+href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex DdgResultLink();
    [GeneratedRegex(@"<a[^>]+class=""[^""]*result__snippet[^""]*""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex DdgSnippet();
    [GeneratedRegex(@"uddg=([^&]+)")] private static partial Regex DdgUddg();
}