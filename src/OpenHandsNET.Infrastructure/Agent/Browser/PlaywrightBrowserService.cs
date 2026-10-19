using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.Browser;

/// <summary>
/// SPEC-18 RF-001/003/004: headless Chromium via Microsoft.Playwright running
/// in the worker process. One <see cref="IBrowserContext"/> per conversation
/// (cookie/storage isolation); binaries auto-install once when missing.
/// </summary>
public sealed class PlaywrightBrowserService(
    IOptions<BrowserOptions> options,
    ILogger<PlaywrightBrowserService> logger) : IBrowserService
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<ContextEntry>>> _contexts = new();
    // SPEC-64 RF-003: storage states a aplicar quando o contexto
    // for (re)criado — attach antes da primeira navegação.
    private readonly ConcurrentDictionary<Guid, string> _pendingStates = new();
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _disposed;

    private sealed record ContextEntry(IBrowserContext Context, IPage Page);

    /// <inheritdoc />
    public async Task<BrowserResult> ActAsync(
        Guid conversationId, BrowserActRequest request, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            return BrowserResult.Fail("browser tool is disabled (Browser:Enabled=false)");
        }

        if (Validate(request) is { } invalid)
        {
            return BrowserResult.Fail(invalid);
        }

        ContextEntry entry;
        try
        {
            entry = await GetContextAsync(conversationId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "browser launch failed for {ConversationId}",
                conversationId);
            return BrowserResult.Fail(
                $"browser unavailable: {ex.Message}. If binaries are missing run " +
                "'playwright install chromium --with-deps' (or enable Browser:AutoInstall).");
        }

        var page = entry.Page;
        try
        {
            switch (request.Action)
            {
                case BrowserActionKind.Goto:
                    await page.GotoAsync(request.Url!, new PageGotoOptions
                    {
                        Timeout = o.NavigationTimeoutMs,
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                    });
                    break;
                case BrowserActionKind.Click:
                    await page.ClickAsync(request.Selector!,
                        new PageClickOptions { Timeout = o.NavigationTimeoutMs });
                    break;
                case BrowserActionKind.Type:
                    await page.FillAsync(request.Selector!, request.Text!,
                        new PageFillOptions { Timeout = o.NavigationTimeoutMs });
                    break;
                case BrowserActionKind.Scroll:
                    await page.Mouse.WheelAsync(
                        0, string.Equals(request.Direction, "up",
                            StringComparison.OrdinalIgnoreCase) ? -800 : 800);
                    break;
                case BrowserActionKind.Back:
                    await page.GoBackAsync(new PageGoBackOptions
                    {
                        Timeout = o.NavigationTimeoutMs,
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                    });
                    break;
                case BrowserActionKind.Forward:
                    await page.GoForwardAsync(new PageGoForwardOptions
                    {
                        Timeout = o.NavigationTimeoutMs,
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                    });
                    break;
                case BrowserActionKind.Screenshot:
                case BrowserActionKind.Content:
                    break;
            }

            var content = request.Action == BrowserActionKind.Content
                ? Truncate(await page.InnerTextAsync("body"), o.MaxContentChars)
                : null;
            var shot = await page.ScreenshotAsync(
                new PageScreenshotOptions { Type = ScreenshotType.Png });
            return new BrowserResult(
                true, page.Url, await page.TitleAsync(),
                Convert.ToBase64String(shot), content, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Contexto morto (target closed / browser caiu) — descarta para a
            // próxima chamada recriar em vez de falhar para sempre.
            _contexts.TryRemove(conversationId, out _);
            try { await entry.Context.CloseAsync(); } catch { }
            return BrowserResult.Fail($"browser action failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(Guid conversationId, CancellationToken ct)
    {
        if (_contexts.TryRemove(conversationId, out var lazy))
        {
            try
            {
                var entry = await lazy.Value;
                await entry.Context.CloseAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "browser context close failed for {Id}",
                    conversationId);
            }
        }
    }

    /// <inheritdoc />
    public async Task<string?> CaptureStorageStateAsync(
        Guid conversationId, CancellationToken ct)
    {
        if (!_contexts.TryGetValue(conversationId, out var lazy)
            || !lazy.IsValueCreated)
        {
            return null;
        }
        var entry = await lazy.Value;
        return await entry.Context.StorageStateAsync();
    }

    /// <inheritdoc />
    public async Task SetStorageStateAsync(
        Guid conversationId, string storageStateJson,
        CancellationToken ct)
    {
        _pendingStates[conversationId] = storageStateJson;
        // contexto existente não carrega state novo — recria para
        // o attach valer já nesta sessão.
        if (_contexts.TryRemove(conversationId, out var lazy)
            && lazy.IsValueCreated)
        {
            var entry = await lazy.Value;
            try { await entry.Context.CloseAsync(); } catch { }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var (id, lazy) in _contexts)
        {
            if (_contexts.TryRemove(id, out _) && lazy.IsValueCreated)
            {
                try { await (await lazy.Value).Context.CloseAsync(); }
                catch { /* shutdown best-effort */ }
            }
        }

        if (_browser is not null)
        {
            try { await _browser.CloseAsync(); } catch { }
        }

        _playwright?.Dispose();
    }

    /// <summary>Argument validation shared by tool + service.</summary>
    internal static string? Validate(BrowserActRequest r) => r.Action switch
    {
        BrowserActionKind.Goto when string.IsNullOrWhiteSpace(r.Url)
            => "goto requires url",
        BrowserActionKind.Click when string.IsNullOrWhiteSpace(r.Selector)
            => "click requires selector",
        BrowserActionKind.Type when string.IsNullOrWhiteSpace(r.Selector)
            => "type requires selector",
        BrowserActionKind.Type when r.Text is null
            => "type requires text",
        _ => null,
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];

    private async Task<ContextEntry> GetContextAsync(
        Guid conversationId, CancellationToken ct)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PlaywrightBrowserService));
        }

        var lazy = _contexts.GetOrAdd(conversationId, id => new Lazy<Task<ContextEntry>>(
            async () =>
            {
                var browser = await EnsureLaunchedAsync(ct);
                var o = options.Value;
                _pendingStates.TryGetValue(id, out var pendingState);
                var context = await browser.NewContextAsync(
                    new BrowserNewContextOptions
                    {
                        ViewportSize = new ViewportSize
                        {
                            Width = o.ViewportWidth, Height = o.ViewportHeight,
                        },
                        StorageState = pendingState,
                    });
                var page = await context.NewPageAsync();
                page.SetDefaultTimeout(o.NavigationTimeoutMs);
                return new ContextEntry(context, page);
            }));
        try
        {
            return await lazy.Value;
        }
        catch
        {
            // Task faulted não pode ficar cacheado — a próxima ação recriaria.
            _contexts.TryRemove(new KeyValuePair<Guid, Lazy<Task<ContextEntry>>>(
                conversationId, lazy));
            throw;
        }
    }

    private async Task<IBrowser> EnsureLaunchedAsync(CancellationToken ct)
    {
        await _launchGate.WaitAsync(ct);
        try
        {
            if (_browser is { IsConnected: true })
            {
                return _browser;
            }

            _playwright ??= await Playwright.CreateAsync();
            try
            {
                _browser = await _playwright.Chromium.LaunchAsync(
                    new BrowserTypeLaunchOptions
                    {
                        Headless = options.Value.Headless,
                    });
            }
            catch (PlaywrightException ex) when (MissingBrowser(ex)
                && options.Value.AutoInstall)
            {
                logger.LogInformation(
                    "Playwright browsers missing — installing chromium");
                var code = await Task.Run(
                    () => Program.Main(["install", "chromium"]), ct);
                if (code != 0)
                {
                    throw new InvalidOperationException(
                        $"playwright install exited {code}");
                }

                _browser = await _playwright.Chromium.LaunchAsync(
                    new BrowserTypeLaunchOptions
                    {
                        Headless = options.Value.Headless,
                    });
            }

            return _browser;
        }
        finally
        {
            _launchGate.Release();
        }
    }

    private static bool MissingBrowser(PlaywrightException ex) =>
        ex.Message.Contains("Executable doesn't exist", StringComparison.Ordinal)
        || ex.Message.Contains("executable doesn't exist", StringComparison.Ordinal)
        || ex.Message.Contains("Please run", StringComparison.Ordinal);
}
