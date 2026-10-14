namespace OpenHandsNET.Application.Agent;

/// <summary>Browser actions exposed by the <c>browser</c> tool (SPEC-18 RF-002).</summary>
public enum BrowserActionKind
{
    /// <summary>Navigate to <c>url</c>.</summary>
    Goto,

    /// <summary>Click the element matching <c>selector</c>.</summary>
    Click,

    /// <summary>Fill the input matching <c>selector</c> with <c>text</c>.</summary>
    Type,

    /// <summary>Scroll the page (<c>direction</c>: up|down).</summary>
    Scroll,

    /// <summary>History back.</summary>
    Back,

    /// <summary>History forward.</summary>
    Forward,

    /// <summary>Screenshot only (no navigation).</summary>
    Screenshot,

    /// <summary>Extract visible page text (innerText, capped).</summary>
    Content,
}

/// <summary>One browser action request.</summary>
public sealed record BrowserActRequest(
    BrowserActionKind Action,
    string? Url = null,
    string? Selector = null,
    string? Text = null,
    string? Direction = null);

/// <summary>Outcome of a browser action (screenshot included on success).</summary>
public sealed record BrowserResult(
    bool Ok,
    string? Url,
    string? Title,
    string? ScreenshotBase64,
    string? Content,
    string? Error)
{
    /// <summary>Builds a failure result.</summary>
    public static BrowserResult Fail(string error) =>
        new(false, null, null, null, null, error);
}

/// <summary>
/// Worker-side headless browser shared across tools (SPEC-18 RF-001).
/// One isolated browser context per conversation; created lazily and
/// closed when the conversation is deleted.
/// </summary>
public interface IBrowserService : IAsyncDisposable
{
    /// <summary>Executes one action on the conversation's page.</summary>
    Task<BrowserResult> ActAsync(
        Guid conversationId, BrowserActRequest request, CancellationToken ct);

    /// <summary>Closes the conversation's browser context (idempotent).</summary>
    Task CloseAsync(Guid conversationId, CancellationToken ct);
}
