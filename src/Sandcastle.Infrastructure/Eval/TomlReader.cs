using System.Text;

namespace Sandcastle.Infrastructure.Eval;

/// <summary>
/// SPEC-152 RF-003: leitor TOML mínimo para <c>task.toml</c> do
/// Terminal-Bench 2.0 — seções <c>[a.b]</c>, <c>key = value</c>
/// (string simples/dupla, inteiro, float, bool, array de strings,
/// inline table com pares string). Sem tabelas aninhadas/arrays de
/// tabelas — suficiente para o formato TB2 sem trazer uma dependência
/// TOML nova para o repo.
/// </summary>
public sealed class TomlReader
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.Ordinal);

    private TomlReader() { }

    /// <summary>Parses TOML text — throws <see cref="FormatException"/> on bad input.</summary>
    public static TomlReader Parse(string content)
    {
        var doc = new TomlReader();
        var section = "";
        doc._sections[section] = new Dictionary<string, string>(
            StringComparer.Ordinal);
        var lineNo = 0;
        foreach (var rawLine in content
            .Replace("\r\n", "\n").Split('\n'))
        {
            lineNo++;
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']') || line.StartsWith("[["))
                {
                    throw new FormatException(
                        $"toml line {lineNo}: unsupported section '{line}'");
                }

                section = line[1..^1].Trim();
                if (section.Length == 0)
                {
                    throw new FormatException(
                        $"toml line {lineNo}: empty section");
                }

                if (!doc._sections.ContainsKey(section))
                {
                    doc._sections[section] = new Dictionary<string, string>(
                        StringComparer.Ordinal);
                }

                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                throw new FormatException(
                    $"toml line {lineNo}: expected 'key = value'");
            }

            var key = line[..eq].Trim().Trim('"');
            var value = line[(eq + 1)..].Trim();
            doc._sections[section][key] = value;
        }

        return doc;
    }

    /// <summary>String scalar at <c>section.key</c> (quoted strings unwrapped).</summary>
    public string? GetString(string section, string key) =>
        TryGet(section, key, out var raw)
            ? Unquote(raw) : null;

    /// <summary>Integer at <c>section.key</c>.</summary>
    public int? GetInt(string section, string key) =>
        TryGet(section, key, out var raw)
            && int.TryParse(raw.Trim(), out var v) ? v : null;

    /// <summary>Float at <c>section.key</c>.</summary>
    public double? GetDouble(string section, string key) =>
        TryGet(section, key, out var raw)
            && double.TryParse(raw.Trim(),
                System.Globalization.CultureInfo.InvariantCulture,
                out var v) ? v : null;

    /// <summary>Boolean at <c>section.key</c>.</summary>
    public bool? GetBool(string section, string key) =>
        TryGet(section, key, out var raw) ? raw.Trim() switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        } : null;

    /// <summary>String array <c>["a", "b"]</c> at <c>section.key</c>.</summary>
    public IReadOnlyList<string>? GetStringArray(string section, string key)
    {
        if (!TryGet(section, key, out var raw)
            || !raw.StartsWith('[') || !raw.EndsWith(']'))
        {
            return null;
        }

        var inner = raw[1..^1].Trim();
        if (inner.Length == 0)
        {
            return [];
        }

        var items = new List<string>();
        foreach (var part in SplitTopLevel(inner))
        {
            var unquoted = Unquote(part.Trim());
            if (unquoted is { Length: > 0 })
            {
                items.Add(unquoted);
            }
        }

        return items;
    }

    /// <summary>
    /// Inline table <c>{ k = "v", ... }</c> → dictionary of string values.
    /// </summary>
    public IReadOnlyDictionary<string, string>? GetInlineTable(
        string section, string key)
    {
        if (!TryGet(section, key, out var raw)
            || !raw.StartsWith('{') || !raw.EndsWith('}'))
        {
            return null;
        }

        var inner = raw[1..^1].Trim();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (inner.Length == 0)
        {
            return map;
        }

        foreach (var part in SplitTopLevel(inner))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            map[part[..eq].Trim().Trim('"')] =
                Unquote(part[(eq + 1)..].Trim()) ?? "";
        }

        return map;
    }

    /// <summary>All keys present in a section (null = section absent).</summary>
    public IReadOnlyCollection<string>? SectionKeys(string section) =>
        _sections.TryGetValue(section, out var s) ? s.Keys : null;

    private bool TryGet(string section, string key, out string raw)
    {
        raw = "";
        return _sections.TryGetValue(section, out var s)
            && s.TryGetValue(key, out raw!);
    }

    private static string? Unquote(string raw)
    {
        var s = raw.Trim();
        if (s.Length >= 2 && s.StartsWith('"') && s.EndsWith('"'))
        {
            return Unescape(s[1..^1]);
        }

        if (s.Length >= 2 && s.StartsWith('\'') && s.EndsWith('\''))
        {
            return s[1..^1];
        }

        return s.Length == 0 ? null : s;
    }

    private static string Unescape(string s) => s
        .Replace("\\n", "\n").Replace("\\t", "\t")
        .Replace("\\\"", "\"").Replace("\\\\", "\\");

    /// <summary>Strips a <c>#</c> comment outside of quoted strings.</summary>
    private static string StripComment(string line)
    {
        var sb = new StringBuilder(line.Length);
        var inStr = false;
        var strCh = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inStr)
            {
                sb.Append(c);
                if (c == strCh && line[i - 1] != '\\')
                {
                    inStr = false;
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                inStr = true;
                strCh = c;
                sb.Append(c);
                continue;
            }

            if (c == '#')
            {
                break;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Splits a comma list, respecting quoted strings.</summary>
    private static IEnumerable<string> SplitTopLevel(string s)
    {
        var start = 0;
        var inStr = false;
        var strCh = '\0';
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr)
            {
                if (c == strCh && (i == 0 || s[i - 1] != '\\'))
                {
                    inStr = false;
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                inStr = true;
                strCh = c;
            }
            else if (c == ',')
            {
                yield return s[start..i];
                start = i + 1;
            }
        }

        yield return s[start..];
    }
}