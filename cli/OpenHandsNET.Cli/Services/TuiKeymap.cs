using System.Text.Json;

namespace OpenHandsNET.Cli.Services;

/// <summary>
/// SPEC-31 RF-003: keybindings do TUI — <c>~/.openhandsnet/tui.json</c>
/// <c>{"aliases":{"/q":"/exit", ...}}</c> mapeia atalhos para slash
/// commands; ausente/inválido → defaults (no-op).
/// </summary>
public sealed class TuiKeymap
{
    private readonly Dictionary<string, string> _aliases;

    private TuiKeymap(Dictionary<string, string> aliases) =>
        _aliases = aliases;

    /// <summary>Aliases vazios (defaults).</summary>
    public static readonly TuiKeymap Empty = new(new());

    /// <summary>Carrega <c>~/.openhandsnet/tui.json</c> (ou <paramref name="path"/>).</summary>
    public static async Task<TuiKeymap> LoadAsync(
        string? path = null, CancellationToken ct = default)
    {
        var file = path ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
            ".openhandsnet", "tui.json");
        if (!File.Exists(file))
        {
            return Empty;
        }

        try
        {
            var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("aliases", out var a)
                && a.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in a.EnumerateObject())
                {
                    if (kv.Value.ValueKind == JsonValueKind.String
                        && kv.Name.StartsWith('/')
                        && kv.Value.GetString() is { } target
                        && target.StartsWith('/'))
                    {
                        map[kv.Name] = target;
                    }
                }
            }

            return new TuiKeymap(map);
        }
        catch (Exception)
        {
            return Empty; // arquivo inválido não derruba o TUI
        }
    }

    /// <summary>Resolve o input para o slash command alvo (ou o próprio).</summary>
    public string Resolve(string input) =>
        _aliases.TryGetValue(input.Trim(), out var target) ? target : input;

    /// <summary>Aliases carregados (para /help e testes).</summary>
    public IReadOnlyDictionary<string, string> Aliases => _aliases;
}
