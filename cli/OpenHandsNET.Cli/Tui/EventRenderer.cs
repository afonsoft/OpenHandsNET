using System.Text.Json;
using Spectre.Console;

namespace OpenHandsNET.Cli.Tui;

/// <summary>Renderizador de eventos V1 no terminal — cor por source/kind.</summary>
public static class EventRenderer
{
    /// <summary>Escreve um evento cru (JsonElement) formatado.</summary>
    public static void Write(JsonElement e)
    {
        var kind = e.TryGetProperty("kind", out var k) ? k.GetString() : "?";
        var source = e.TryGetProperty("source", out var s) ? s.GetString() : "";
        switch (kind)
        {
            case "MessageEvent":
                var role = Role(e);
                var text = MessageText(e);
                AnsiConsole.MarkupLine(role == "user"
                    ? $"[blue on grey15] você [/] {text.EscapeMarkup()}"
                    : $"[green on grey15] agent [/] {text.EscapeMarkup()}");
                break;

            case "ActionEvent":
                var tool = e.TryGetProperty("tool", out var t)
                    && t.TryGetProperty("name", out var n)
                        ? n.GetString() : "?";
                var cmd = e.TryGetProperty("tool", out var t2)
                    && t2.TryGetProperty("arguments", out var a)
                    && a.TryGetProperty("command", out var c)
                        ? c.GetString() : null;
                AnsiConsole.MarkupLine(
                    $"[purple]⚙ {tool}[/]" + (cmd is null ? "" :
                        $" [grey]{cmd.EscapeMarkup()}[/]"));
                break;

            case "ObservationEvent":
                var obs = e.TryGetProperty("observation", out var o)
                    ? o : default;
                var output = obs.ValueKind == JsonValueKind.String
                    ? obs.GetString()
                    : obs.ValueKind == JsonValueKind.Object
                        && obs.TryGetProperty("output", out var o2)
                            ? o2.GetString() : null;
                if (output is { Length: > 0 })
                {
                    AnsiConsole.MarkupLine(
                        $"[grey]  └─ {Trim(output, 200).EscapeMarkup()}[/]");
                }
                break;

            case "ConversationStateUpdateEvent":
                var v = e.TryGetProperty("value", out var val) ? val : default;
                var st = v.ValueKind == JsonValueKind.String
                    ? v.GetString() : v.ToString();
                AnsiConsole.MarkupLine($"[yellow]── {st} ──[/]");
                break;

            case "AgentErrorEvent":
            case "ServerErrorEvent":
                var err = e.TryGetProperty("error", out var er)
                    ? er.GetString() : e.ToString();
                AnsiConsole.MarkupLine(
                    $"[red]✗ {kind}: {Trim(err ?? "", 200).EscapeMarkup()}[/]");
                break;

            case "UserRejectObservation":
                AnsiConsole.MarkupLine("[red]✗ action rejected by user[/]");
                break;

            case "HookExecutionEvent":
            case "CondensationEvent":
            case "CondensationRequestEvent":
            case "CondensationSummaryEvent":
                AnsiConsole.MarkupLine($"[grey]· {kind}[/]");
                break;
        }
    }

    /// <summary>Trunca saída longa para o log.</summary>
    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static string Role(JsonElement e) =>
        e.TryGetProperty("llm_message", out var m)
        && m.TryGetProperty("role", out var r)
            ? r.GetString() ?? "user" : "user";

    private static string MessageText(JsonElement e)
    {
        if (!e.TryGetProperty("llm_message", out var m)
            || !m.TryGetProperty("content", out var content))
        {
            return "";
        }

        var sb = new System.Text.StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var t))
            {
                sb.Append(t.GetString());
            }
        }

        return sb.ToString();
    }
}
