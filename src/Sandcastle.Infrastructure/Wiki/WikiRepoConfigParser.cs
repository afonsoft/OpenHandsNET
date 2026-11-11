using System.Text.Json;

using Sandcastle.Application.Wiki;

namespace Sandcastle.Infrastructure.Wiki;

/// <summary>
/// Parser de <c>.sandcastle/wiki.json</c> (SPEC-145 RF-002) — tolerante:
/// JSON mal formado devolve config vazia (nunca bloqueia a geração).
/// </summary>
public static class WikiRepoConfigParser
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Lê a config do workdir (null-safe).</summary>
    public static async Task<WikiRepoConfig> ReadAsync(
        string rootPath, CancellationToken ct)
    {
        var path = Path.Combine(
            rootPath, WikiRepoConfig.ConfigPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            return new WikiRepoConfig();
        }
        try
        {
            var text = await File.ReadAllTextAsync(path, ct);
            return Parse(text);
        }
        catch (Exception)
        {
            return new WikiRepoConfig();
        }
    }

    /// <summary>Parse tolerante do JSON (doc-friendly).</summary>
    public static WikiRepoConfig Parse(string json)
    {
        try
        {
            var wire = JsonSerializer.Deserialize<Wire>(json, Json);
            if (wire is null)
            {
                return new WikiRepoConfig();
            }
            return new WikiRepoConfig(
                Include: Clean(wire.Include),
                Exclude: Clean(wire.Exclude),
                Language: wire.Language,
                Effort: wire.Effort,
                TopicsHint: Clean(wire.TopicsHint),
                AutoUpdate: wire.AutoUpdate);
        }
        catch (JsonException)
        {
            return new WikiRepoConfig();
        }
    }

    private static IReadOnlyList<string>? Clean(List<string>? items) =>
        items is { Count: > 0 } list
            ? list.Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim()).ToList()
            : null;

    /// <summary>Wire shape da config (snake_case tolerante).</summary>
    private sealed class Wire
    {
        public List<string>? Include { get; set; }
        public List<string>? Exclude { get; set; }
        public string? Language { get; set; }
        public string? Effort { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("topics_hint")]
        public List<string>? TopicsHint { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("auto_update")]
        public bool AutoUpdate { get; set; }
    }
}