using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;

namespace OpenHandsNET.Client.Services;

/// <summary>
/// SPEC-46 RF-001: localização do cliente — dicionários JSON em
/// <c>wwwroot/i18n/{lang}.json</c>, cadeia de fallback
/// <c>active → en-US → pt-BR → key</c>, interpolação
/// <c>{{name}}</c> e evento <see cref="Changed"/> para re-render
/// sem reload. Port do <c>LocalizationService</c> do open-webui (MIT).
/// </summary>
public sealed class LocalizationService(HttpClient http, IJSRuntime js)
{
    /// <summary>Idioma padrão do produto e último fallback antes da chave.</summary>
    public const string DefaultLanguage = "pt-BR";

    /// <summary>Chave do localStorage onde o idioma escolhido persiste.</summary>
    public const string StorageKey = "ohn.locale";

    /// <summary>Idiomas padrão caso <c>i18n/locales.json</c> não carregue.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> DefaultLanguages =
    [
        ("pt-BR", "Português (Brasil)"),
        ("en-US", "English (US)"),
    ];

    /// <summary>Idiomas do manifesto <c>i18n/locales.json</c>
    /// (fallback para <see cref="DefaultLanguages"/>).</summary>
    public IReadOnlyList<(string Code, string Name)> Languages { get; private set; } =
        DefaultLanguages;

    private Dictionary<string, string> _active = new();
    private Dictionary<string, string> _fallbackEn = new();
    private Dictionary<string, string> _fallbackPt = new();

    /// <summary>Idioma ativo (BCP-47, ex.: "pt-BR").</summary>
    public string Language { get; private set; } = DefaultLanguage;

    /// <summary>Cultura do idioma ativo — datas/números.</summary>
    public CultureInfo Culture => new(Language);

    /// <summary>Data e hora curtas na cultura ativa.</summary>
    public string FormatDateTime(DateTime value) => value.ToString("g", Culture);

    /// <summary>Somente a data na cultura ativa.</summary>
    public string FormatDate(DateTime value) => value.ToString("d", Culture);

    /// <summary>Disparado quando o idioma muda — MainLayout re-renderiza.</summary>
    public event Action? Changed;

    /// <summary>Resolve uma chave para a string localizada.</summary>
    public string this[string key] => T(key);

    /// <summary>Fallback: idioma ativo → en-US → pt-BR → a própria chave.</summary>
    public string T(string key) =>
        _active.TryGetValue(key, out var value)
        || _fallbackEn.TryGetValue(key, out value)
        || _fallbackPt.TryGetValue(key, out value)
            ? value
            : key;

    /// <summary>Resolve a chave e interpola placeholders <c>{{nome}}</c>.</summary>
    public string T(string key, params (string Name, string Value)[] args)
    {
        var text = T(key);
        foreach (var (name, value) in args)
        {
            text = text.Replace("{{" + name + "}}", value,
                StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>Carrega fallbacks + manifesto + idioma persistido
    /// (chamado antes do primeiro render).</summary>
    public async Task InitializeAsync()
    {
        _fallbackPt = await LoadAsync(DefaultLanguage);
        _fallbackEn = await LoadAsync("en-US");
        await LoadManifestAsync();
        string? stored = null;
        try
        {
            stored = await js.InvokeAsync<string?>(
                "localStorage.getItem", StorageKey);
        }
        catch (JSException)
        {
            // localStorage indisponível — segue com o padrão.
        }

        await ApplyAsync(stored);
    }

    /// <summary>Troca o idioma, persiste e notifica a UI.</summary>
    public async Task SetLanguageAsync(string language)
    {
        await ApplyAsync(language);
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem",
                StorageKey, Language);
        }
        catch (JSException)
        {
            // Sem persistência — troca ainda vale para a sessão.
        }

        Changed?.Invoke();
    }

    private async Task ApplyAsync(string? language)
    {
        var lang = Languages.Any(l => l.Code == language)
            ? language! : DefaultLanguage;
        _active = lang == DefaultLanguage
            ? _fallbackPt : await LoadAsync(lang);
        Language = lang;
        try
        {
            // Mantém <html lang> consistente (leitores de tela).
            await js.InvokeVoidAsync("ohnI18n.setLang", lang);
        }
        catch (JSException)
        {
            // JS indisponível — mantém o idioma mesmo assim.
        }
        catch (InvalidOperationException)
        {
            // JS ainda indisponível durante o prerender.
        }
    }

    private async Task LoadManifestAsync()
    {
        try
        {
            var manifest = await http.GetFromJsonAsync<LocalesManifest>(
                "i18n/locales.json");
            if (manifest?.Locales is { Count: > 0 } locales)
            {
                Languages = locales
                    .Select(l => (l.Code, l.Name))
                    .Where(l => !string.IsNullOrWhiteSpace(l.Code))
                    .ToList();
            }
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            // Manifesto indisponível — mantém DefaultLanguages.
        }
    }

    private sealed record LocaleEntry(string Code, string Name);

    private sealed record LocalesManifest(List<LocaleEntry> Locales);

    private async Task<Dictionary<string, string>> LoadAsync(string language)
    {
        try
        {
            return await http.GetFromJsonAsync<Dictionary<string, string>>(
                $"i18n/{language}.json") ?? [];
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return [];
        }
    }

    private static bool IsTransient(Exception ex) => ex is
        HttpRequestException or OperationCanceledException or JSException
        or JsonException or NotSupportedException;
}
