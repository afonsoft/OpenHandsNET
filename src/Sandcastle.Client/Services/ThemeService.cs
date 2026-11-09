using System.Net.Http.Json;

using Microsoft.JSInterop;

namespace Sandcastle.Client.Services;

/// <summary>
/// SPEC-26 RF-001: tema dark|light|system. Persiste em
/// <c>localStorage("sc.theme")</c> (sync, zero flash via ThemeInit no
/// index.html) e em <c>PUT /users/me/preferences</c> (best-effort).
/// <c>system</c> segue <c>prefers-color-scheme</c> via listener JS.
/// </summary>
public sealed class ThemeService : IAsyncDisposable
{
    public const string Dark = "dark";
    public const string Light = "light";
    public const string System = "system";

    private readonly IJSRuntime _js;
    private readonly SandcastleApiClient _api;
    private readonly HttpClient _http;
    private IJSObjectReference? _selfRef;

    public ThemeService(IJSRuntime js, SandcastleApiClient api, HttpClient http)
    {
        _js = js;
        _api = api;
        _http = http;
    }

    /// <summary>Tema selecionado (sempre resolvido após <see cref="InitAsync"/>).</summary>
    public string Theme { get; private set; } = Light;

    /// <summary>Tema efetivo aplicado (dark/light — nunca "system").</summary>
    public string EffectiveTheme { get; private set; } = Light;

    /// <summary>Dispara quando o tema efetivo muda.</summary>
    public event Action? Changed;

    /// <summary>
    /// Lê localStorage → server (server vence se houver valor) → aplica.
    /// Chamado uma vez no boot do <see cref="App"/>.
    /// </summary>
    public async Task InitAsync()
    {
        var stored = await SafeJs<string?>("scTheme.get");
        Theme = Valid(stored) ? stored! : Light;

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
        Theme = Valid(theme) ? theme : Light;
        await ApplyAsync();
        await SafeJs("scTheme.set", Theme);
        _ = PersistRemoteAsync();
    }

    private async Task ApplyAsync()
    {
        EffectiveTheme = Theme == System
            ? await SafeJs<string>("scTheme.system") ?? Light
            : Theme;
        _selfRef ??= await SafeJs<IJSObjectReference?>(
            "scTheme.watch", DotNetObjectReference.Create(this));
        await SafeJs("scTheme.apply", EffectiveTheme);
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
        await SafeJs("scTheme.apply", EffectiveTheme);
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

    // ---- SPEC-116: paletas custom (themes/builtin.json) ----

    /// <summary>Id da paleta ativa (<c>default</c> = stock, sem overrides).</summary>
    public string PaletteId { get; private set; } = "default";

    private List<CustomThemeDefinition> _palettes = [];

    /// <summary>Presets carregados de <c>wwwroot/themes/builtin.json</c>.</summary>
    public IReadOnlyList<CustomThemeDefinition> Palettes => _palettes;

    /// <summary>Carrega os presets; idempotente.</summary>
    public async Task LoadPalettesAsync()
    {
        if (_palettes.Count > 0)
        {
            return;
        }

        try
        {
            var list = await _http.GetFromJsonAsync<List<CustomThemeDefinition>>(
                "themes/builtin.json");
            if (list is not null)
            {
                _palettes = list;
            }
        }
        catch (HttpRequestException) { /* offline — catálogo vazio */ }
    }

    /// <summary>
    /// Define a paleta ativa: gera o CSS via <see cref="ThemeCssBuilder"/> e
    /// injeta <c>&lt;style id="user-theme"&gt;</c> + classe
    /// <c>theme-{id}</c> no <c>&lt;html&gt;</c>.
    /// </summary>
    public async Task SetPaletteAsync(string paletteId)
    {
        PaletteId = paletteId;
        var palette = _palettes.FirstOrDefault(p => p.Id == paletteId);
        var css = palette is null || palette.Id == "default"
            ? ""
            : ThemeCssBuilder.Build(palette);
        await SafeJs("scTheme.setCustom", palette?.Id ?? "default", css);
        Changed?.Invoke();
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
            await _js.InvokeVoidAsync("scTheme.unwatch");
            if (_selfRef is not null)
            {
                await _selfRef.DisposeAsync();
            }
        }
        catch (JSDisconnectedException) { /* best-effort — falha tolerada */ }
    }
}