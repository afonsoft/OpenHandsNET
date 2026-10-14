namespace OpenHandsNET.Application.Web;

/// <summary>
/// SPEC-25 RF-003: backend-side fetch for the <c>web_fetch</c> tool —
/// the request goes out via the worker egress (never the sandbox), with
/// an SSRF guard and the egress allowlist applied when enabled.
/// </summary>
public interface IWebFetchService
{
    /// <summary>Fetches <paramref name="url"/> (http/https only).</summary>
    Task<WebFetchResult> FetchAsync(
        string url, int maxChars, CancellationToken ct);
}

/// <summary>Outcome of a web fetch.</summary>
public sealed record WebFetchResult(
    bool Ok, string? FinalUrl, int StatusCode, string? Text, string? Error);
