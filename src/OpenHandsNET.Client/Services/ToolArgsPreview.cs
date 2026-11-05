using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenHandsNET.Client.Services;

/// <summary>
/// Extrai campos-chave do JSON de args de uma tool call
/// (SPEC-120, port OWUIC ToolArgsPreview). O preview pode vir truncado —
/// primeiro tenta <see cref="JsonDocument"/> e, se falhar, cai num regex
/// tolerante por campo (<c>"key": "value"</c>).
/// </summary>
public static class ToolArgsPreview
{
    /// <summary>Campo-chave usado como resumo por tool.</summary>
    private static readonly (string Tool, string Field)[] SummaryFields =
    [
        ("bash", "command"),
        ("execute_bash", "command"),
        ("web_fetch", "url"),
        ("browser", "url"),
        ("search", "query"),
        ("file_read", "path"),
        ("file_write", "path"),
        ("file_edit", "path"),
        ("file_list", "path"),
        ("file_grep", "pattern"),
        ("file_glob", "pattern"),
        ("apply_patch", "path"),
        ("delegate_task", "task"),
        ("create_pull_request", "title"),
        ("git_commit", "message"),
    ];

    /// <summary>
    /// Resumo de uma linha para o cabeçalho do card — o campo principal
    /// da tool conhecida (ex.: comando do bash).
    /// </summary>
    public static string? Summary(string toolName, string? argsJson)
    {
        var suffix = toolName.Split(':', '/', StringSplitOptions.None)[^1];
        var field = SummaryFields.FirstOrDefault(f => f.Tool == suffix).Field;
        return field is null ? null : TryGetField(argsJson, field);
    }

    /// <summary>
    /// Valor de um campo string de primeiro nível do JSON de args;
    /// tolera preview truncado via regex quando o parse falha.
    /// </summary>
    public static string? TryGetField(string? argsJson, string field)
    {
        if (string.IsNullOrEmpty(argsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(field, out var el))
            {
                return el.ValueKind == JsonValueKind.String
                    ? el.GetString()
                    : el.GetRawText();
            }
            return null;
        }
        catch (JsonException)
        {
            // Preview truncado no meio do JSON — regex tolerante.
        }

        var match = FieldRegex(field).Match(argsJson);
        return match.Success ? Unescape(match.Groups[1].Value) : null;
    }

    private static string Unescape(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<string>($"\"{raw}\"") ?? raw;
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private static Regex FieldRegex(string field) =>
        new($"\"{Regex.Escape(field)}\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
}
