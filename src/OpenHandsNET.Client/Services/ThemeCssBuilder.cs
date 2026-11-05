using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenHandsNET.Client.Services;

/// <summary>
/// SPEC-116: definição de paleta custom (presets de <c>wwwroot/themes/builtin.json</c>).
/// Tokens semânticos viram variáveis CSS consumidas pela UI.
/// </summary>
/// <param name="Id">Slug único do tema (<c>theme-{id}</c> no <c>&lt;html&gt;</c>).</param>
/// <param name="Name">Nome exibido no seletor.</param>
/// <param name="Dark">Paleta do modo escuro.</param>
/// <param name="Light">Paleta do modo claro.</param>
/// <param name="Vars">Overrides crus de variáveis CSS (<c>--x: valor</c>), aplicados por último.</param>
public sealed record CustomThemeDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("dark")] IReadOnlyDictionary<string, string> Dark,
    [property: JsonPropertyName("light")] IReadOnlyDictionary<string, string> Light,
    [property: JsonPropertyName("vars")] IReadOnlyDictionary<string, string> Vars);

/// <summary>
/// Gera o CSS de uma paleta custom: variáveis <c>--ohn-*</c> por modo
/// (<c>.theme-{id}[data-theme=light|dark]</c>) + overrides crus em
/// <c>.theme-{id}</c>. Tokens semânticos aceitos: bg, surface, muted,
/// text, border, accent, code-bg, danger.
/// </summary>
public static class ThemeCssBuilder
{
    private static readonly (string Token, string Var)[] SemanticMap =
    [
        ("bg", "--ohn-bg"),
        ("surface", "--ohn-surface"),
        ("muted", "--ohn-muted"),
        ("text", "--ohn-text"),
        ("border", "--ohn-border"),
        ("accent", "--ohn-accent"),
        ("code-bg", "--ohn-code-bg"),
        ("danger", "--ohn-danger"),
    ];

    /// <summary>Monta o CSS completo do tema (escopo <c>.theme-{id}</c>).</summary>
    public static string Build(CustomThemeDefinition theme)
    {
        var css = new StringBuilder(1024);
        WritePalette(css, theme.Id, "light", theme.Light);
        WritePalette(css, theme.Id, "dark", theme.Dark);
        if (theme.Vars.Count > 0)
        {
            css.Append(".theme-").Append(theme.Id).Append(" {\n");
            foreach (var (k, v) in theme.Vars)
            {
                if (k.StartsWith("--", StringComparison.Ordinal))
                {
                    css.Append("  ").Append(k).Append(": ").Append(v).Append(";\n");
                }
            }

            css.Append("}\n");
        }

        return css.ToString();
    }

    private static void WritePalette(
        StringBuilder css, string id, string mode,
        IReadOnlyDictionary<string, string> palette)
    {
        if (palette.Count == 0)
        {
            return;
        }

        css.Append(".theme-").Append(id).Append("[data-theme='").Append(mode)
            .Append("'] {\n");
        foreach (var (token, var) in SemanticMap)
        {
            if (palette.TryGetValue(token, out var value))
            {
                css.Append("  ").Append(var).Append(": ").Append(value).Append(";\n");
            }
        }

        css.Append("}\n");
    }
}
