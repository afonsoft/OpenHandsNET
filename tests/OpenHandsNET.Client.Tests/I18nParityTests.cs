using System.Text.Json;

using Shouldly;

using Xunit;

namespace OpenHandsNET.Client.Tests;

/// <summary>
/// SPEC-46 RF-005: paridade de chaves entre dicionários i18n —
/// en-US/pt-BR completos e não-vazios, demais locales ⊆ en-US, manifesto consistente.
/// </summary>
public class I18nParityTests
{
    private static string I18nDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "OpenHandsNET.Client", "wwwroot", "i18n");
            if (Directory.Exists(candidate)) { return candidate; }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("wwwroot/i18n não encontrado subindo de " + AppContext.BaseDirectory);
    }

    private static Dictionary<string, string> Load(string code)
    {
        var path = Path.Combine(I18nDir(), $"{code}.json");
        File.Exists(path).ShouldBeTrue($"locale file ausente: {code}.json");
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        dict.ShouldNotBeNull($"{code}.json inválido");
        return dict;
    }

    [Fact]
    public void Manifest_Files_Match()
    {
        // AC: cada locale do manifesto tem arquivo e vice-versa
        var manifestPath = Path.Combine(I18nDir(), "locales.json");
        File.Exists(manifestPath).ShouldBeTrue("locales.json ausente");
        var manifest = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(manifestPath));
        var codes = manifest.GetProperty("locales").EnumerateArray()
            .Select(e => e.GetProperty("code").GetString()!).OrderBy(c => c).ToList();

        var files = Directory.GetFiles(I18nDir(), "*.json")
            .Select(Path.GetFileNameWithoutExtension).Where(n => n != "locales")
            .Select(n => n!).OrderBy(n => n).ToList();

        codes.ShouldBe(files);
        codes.ShouldContain("en-US");
        codes.ShouldContain("pt-BR");
    }

    [Fact]
    public void EnUS_And_PtBR_Have_Identical_NonEmpty_Keys()
    {
        var en = Load("en-US");
        var pt = Load("pt-BR");

        en.Keys.OrderBy(k => k).ShouldBe(pt.Keys.OrderBy(k => k),
            "pt-BR deve espelhar o keyset de en-US");

        foreach (var (k, v) in en)
        {
            v.ShouldNotBeNullOrWhiteSpace($"en-US[{k}] vazio");
            pt[k].ShouldNotBeNullOrWhiteSpace($"pt-BR[{k}] vazio");
        }
    }

    [Fact]
    public void Other_Locales_Do_Not_Introduce_Keys_Absent_From_EnUS()
    {
        var enKeys = Load("en-US").Keys.ToHashSet();
        var extras = new[] { "es-ES", "fr-FR", "de-DE", "it-IT", "ja-JP", "zh-CN" };

        foreach (var code in extras)
        {
            var dict = Load(code);
            var alien = dict.Keys.Where(k => !enKeys.Contains(k)).ToList();
            alien.ShouldBeEmpty($"{code} tem chaves ausentes de en-US: {string.Join(", ", alien)}");
            foreach (var (k, v) in dict)
            {
                v.ShouldNotBeNullOrWhiteSpace($"{code}[{k}] vazio");
            }
        }
    }

    [Fact]
    public void Placeholders_Match_Between_EnUS_And_PtBR()
    {
        // interpolação {{name}} deve bater — senão L.T deixa placeholders por resolver
        var en = Load("en-US");
        var pt = Load("pt-BR");
        var rx = new System.Text.RegularExpressions.Regex(@"\{\{(\w+)\}\}");

        foreach (var (k, v) in en)
        {
            var enArgs = rx.Matches(v).Select(m => m.Groups[1].Value).OrderBy(x => x).ToList();
            var ptArgs = rx.Matches(pt[k]).Select(m => m.Groups[1].Value).OrderBy(x => x).ToList();
            ptArgs.ShouldBe(enArgs, $"placeholders divergem em '{k}'");
        }
    }
}