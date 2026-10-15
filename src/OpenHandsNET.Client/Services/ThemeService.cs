using Microsoft.JSInterop;

namespace OpenHandsNET.Client.Services;

/// <summary>
/// SPEC-26 RF-001: tema dark|light|system. Persiste em
/// <c>localStorage("ohn.theme")</c> (sync, zero flash via ThemeInit no
/// index.html) e em <c>PUT /users/me/preferences</c> (best-effort).
/// <c>system</c> segue <c>prefers-color-scheme</c> via listener JS.
/// </summary>
public sealed class ThemeService : IAsyncDisposable
{
    public const string Dark = "dark";
    public const string Light = "light";
    public const string System = "system";

    private readonly IJSRuntime _js;
    private readonly OpenHandsNetApiClient _api;
    private IJSObjectReference? _selfRef;

    public ThemeService(IJSRuntime js, OpenHandsNetApiClient api)
    {
        _js = js;
        _api = api;
    }

    /// <summary>Tema selecionado (sempre resolvido após <see cref="InitAsync"/>).</summary>
    public string Theme { get; private set; } = Dark;

    /// <summary>Tema efetivo aplicado (dark/light — nunca "system").</summary>
    public string EffectiveTheme { get; private set; } = Dark;

    /// <summary>Dispara quando o tema efetivo muda.</summary>
    public event Action? Changed;

    /// <summary>
    /// Lê localStorage → server (server vence se houver valor) → aplica.
    /// Chamado uma vez no boot do <see cref="App"/>.
    /// </summary>
    public async Task InitAsync()
    {
        var stored = await SafeJs<string?>("ohnTheme.get");
        Theme = Valid(stored) ? stored! : Dark;

        try
        {
            var serverPrefs = await _api.GetPreferencesAsync();
            if (serverPrefs.TryGetValue("theme", out var t) && Valid(t))
            {
                Theme = t;
            }
        }
        catch (HttpRequestException) { /* backend sem auth/offline — local vence */ }

        await ApplyAsync();
    }

    /// <summary>Define o tema, aplica, e persiste local + remoto.</summary>
    public async Task SetAsync(string theme)
    {
        Theme = Valid(theme) ? theme : Dark;
        await ApplyAsync();
        await SafeJs("ohnTheme.set", Theme);
        _ = PersistRemoteAsync();
    }

    private async Task ApplyAsync()
    {
        EffectiveTheme = Theme == System
            ? await SafeJs<string>("ohnTheme.system") ?? Dark
            : Theme;
        _selfRef ??= await SafeJs<IJSObjectReference?>(
            "ohnTheme.watch", DotNetObjectReference.Create(this));
        await SafeJs("ohnTheme.apply", EffectiveTheme);
        Changed?.Invoke();
    }

    /// <summary>Callback do listener <c>prefers-color-scheme</c>.</summary>
    [JSInvokable]
    public async Task OnSystemThemeChanged(string theme)
    {
        if (Theme != System || EffectiveTheme == theme)
        {
            return;
        }

        EffectiveTheme = theme;
        await SafeJs("ohnTheme.apply", EffectiveTheme);
        Changed?.Invoke();
    }

    private async Task PersistRemoteAsync()
    {
        try
        {
            await _api.PutPreferencesAsync(
                new Dictionary<string, string> { ["theme"] = Theme });
        }
        catch (HttpRequestException) { /* offline — local já persistiu */ }
    }

    private async Task<T?> SafeJs<T>(string id, params object?[] args)
    {
        try
        {
            return await _js.InvokeAsync<T>(id, args);
        }
        catch (JSException) { return default; }
        catch (InvalidOperationException) { return default; } // pré-render
    }

    private Task SafeJs(string id, params object?[] args) =>
        SafeJs<object>(id, args);

    private static bool Valid(string? t) => t is Dark or Light or System;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("ohnTheme.unwatch");
            if (_selfRef is not null)
            {
                await _selfRef.DisposeAsync();
            }
        }
        catch (JSDisconnectedException) { }
    }
}
