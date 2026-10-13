using Spectre.Console;
using Spectre.Console.Cli;
using OpenHandsNET.Cli.Services;
using OpenHandsNET.Cli.Tui;

namespace OpenHandsNET.Cli.Commands;

/// <summary>
/// <c>openhandsnet</c> sem args — TUI interativo: chat + eventos live +
/// slash commands (<c>/new /status /exit</c>). Reconexão automática.
/// </summary>
public sealed class TuiCommand : AsyncCommand<TuiCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }

        [CommandOption("-c|--conversation <ID>")]
        public string? ConversationId { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            AnsiConsole.MarkupLine(
                "[red]TUI requer terminal interativo[/] — use " +
                "[grey]openhandsnet run|cloud|conversations[/] em scripts/CI");
            return 1;
        }

        AnsiConsole.MarkupLine("[bold]OpenHandsNET[/] — interactive TUI " +
            "([grey]/help para comandos[/])");
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: ct);
        string? convId = settings.ConversationId;

        while (!ct.IsCancellationRequested)
        {
            if (convId is null)
            {
                var task = AnsiConsole.Ask<string>(
                    "[green]nova tarefa[/] ([grey]ou /list /exit[/])");
                if (task.StartsWith('/'))
                {
                    if (task is "/exit" or "/quit")
                    {
                        return 0;
                    }

                    if (task is "/list")
                    {
                        await ShowListAsync(api, ct);
                        continue;
                    }

                    if (task is "/help")
                    {
                        ShowHelp();
                        continue;
                    }
                }

                var created = await api.CreateConversationAsync(task, null, ct: ct);
                var taskId = created.GetProperty("id").GetString()!;
                (convId, var err) = await new RunExecutor(api)
                    .WaitReadyAsync(taskId, s =>
                        AnsiConsole.MarkupLine($"[grey]· {s}[/]"), ct);
                if (convId is null)
                {
                    AnsiConsole.MarkupLine($"[red]{err}[/]");
                    continue;
                }

                AnsiConsole.MarkupLine(
                    $"[grey]conversa {convId[..8]} — eventos live abaixo[/]");
            }

            await PumpLoopAsync(api, convId, ct);
            var input = AnsiConsole.Ask<string>("[green]>[/]");
            switch (input.Trim())
            {
                case "/exit" or "/quit":
                    return 0;
                case "/new":
                    convId = null;
                    continue;
                case "/status":
                    var c = await api.GetConversationAsync(convId, ct);
                    AnsiConsole.MarkupLine(c is { } x
                        ? $"[yellow]{x.GetProperty("execution_status").GetString()}[/]"
                        : "[red]not found[/]");
                    continue;
                case "/list":
                    await ShowListAsync(api, ct);
                    continue;
                case "/help":
                    ShowHelp();
                    continue;
                case "":
                    continue;
                default:
                    await api.SendMessageAsync(convId, input, ct);
                    break;
            }
        }

        return 0;
    }

    /// <summary>Drena eventos novos (poll 1.2s) até Enter ou timeout curto.</summary>
    private static async Task PumpLoopAsync(
        CloudApiClient api, string convId, CancellationToken ct)
    {
        var seen = new HashSet<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(3))
        {
            var page = await api.GetEventsAsync(convId, ct: ct);
            if (page.TryGetProperty("items", out var items))
            {
                foreach (var e in items.EnumerateArray())
                {
                    var id = e.TryGetProperty("id", out var i)
                        ? i.GetString() ?? "" : "";
                    if (seen.Add(id))
                    {
                        EventRenderer.Write(e);
                    }
                }
            }

            await Task.Delay(600, ct);
        }
    }

    private static async Task ShowListAsync(
        CloudApiClient api, CancellationToken ct)
    {
        var page = await api.ListConversationsAsync(ct);
        if (page.TryGetProperty("items", out var items))
        {
            foreach (var c in items.EnumerateArray())
            {
                AnsiConsole.MarkupLine(
                    $"  [grey]{c.GetProperty("id").GetString()![..8]}[/] " +
                    $"{(c.TryGetProperty("title", out var t) ? t.GetString() : "?")} " +
                    $"[yellow]{c.GetProperty("execution_status").GetString()}[/]");
            }
        }
    }

    private static void ShowHelp() => AnsiConsole.MarkupLine(
        "[grey]/new[/] nova conversa · [grey]/list[/] listar · " +
        "[grey]/status[/] estado atual · [grey]/exit[/] sair");
}
