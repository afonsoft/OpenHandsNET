namespace OpenHandsNET.Application.Web;

/// <summary>
/// SPEC-133 RF-001: backend-side web search for the <c>web_search</c>
/// tool — the request goes out via the worker egress (never the
/// sandbox). Provider is chosen by <see cref="WebSearchOptions"/>.
/// </summary>
public interface IWebSearchService
{
    /// <summary>Searches the web for <paramref name="query"/>.</summary>
    Task<WebSearchOutcome> SearchAsync(
        string query, int maxResults, CancellationToken ct);
}

/// <summary>A single search hit (untrusted content — display only).</summary>
public sealed record WebSearchResult(string Title, string Url, string Snippet);

/// <summary>Outcome of a web search.</summary>
public sealed record WebSearchOutcome(
    bool Ok, IReadOnlyList<WebSearchResult> Results, string? Error);

/// <summary>Options for <c>WebSearch:*</c> configuration (SPEC-133).</summary>
public sealed class WebSearchOptions
{
    /// <summary>Config section name.</summary>
    public const string Section = "WebSearch";

    /// <summary>duckduckgo (default, no key) | tavily | brave | none.</summary>
    public string Provider { get; set; } = "duckduckgo";

    /// <summary>API key for tavily/brave — never logged or serialized.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Per-request HTTP timeout.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>In-process result cache TTL (seconds).</summary>
    public int CacheSeconds { get; set; } = 300;
}