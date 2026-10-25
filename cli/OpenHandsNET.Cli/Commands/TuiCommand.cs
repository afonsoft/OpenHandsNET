using OpenHandsNET.Cli.Services;
using OpenHandsNET.Cli.Tui;

using Spectre.Console;
using Spectre.Console.Cli;

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
        CommandContext context, Settings settings, CancellationToken ct)
    {
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
        // SPEC-31 RF-003: keybindings de ~/.openhandsnet/tui.json.
        var keys = await TuiKeymap.LoadAsync(ct: ct);
        if (keys.Aliases.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[grey]{keys.Aliases.Count} tui alias(es) carregados[/]");
        }

        string? convId = settings.ConversationId;

        while (!ct.IsCancellationRequested)
        {
            if (convId is null)
            {
                var (created, exit) = await PromptTaskAsync(api, ct);
                if (exit)
                {
                    return 0;
                }

                convId = created;
                if (convId is null)
                {
                    continue;
                }
            }

            await PumpLoopAsync(api, convId, ct);
            var input = await AnsiConsole.AskAsync<string>("[green]>[/]");
            (var nextConvId, var quit) = await DispatchAsync(
                api, keys, input, convId, ct);
            convId = nextConvId;
            if (quit)
            {
                return 0;
            }
        }

        return 0;
    }

    /// <summary>
    /// Prompt da nova tarefa (com /list /help /exit inline) + cria a
    /// conversa e espera READY. Retorna (convId, sairDoLoop).
    /// </summary>
    private static async Task<(string? ConvId, bool Exit)> PromptTaskAsync(
        CloudApiClient api, CancellationToken ct)
    {
        var task = await AnsiConsole.AskAsync<string>(
            "[green]nova tarefa[/] ([grey]ou /list /exit[/])", ct);
        if (task.StartsWith('/'))
        {
            if (task is "/exit" or "/quit")
            {
                return (null, true);
            }

            if (task is "/list")
            {
                await ShowListAsync(api, ct);
                return (null, false);
            }

            if (task is "/help")
            {
                ShowHelp();
                return (null, false);
            }
        }

        var created = await api.CreateConversationAsync(task, null, ct: ct);
        var taskId = created.GetProperty("id").GetString()!;
        var (convId, err) = await new RunExecutor(api)
            .WaitReadyAsync(taskId, s =>
                AnsiConsole.MarkupLine($"[grey]· {s}[/]"), ct);
        if (convId is null)
        {
            AnsiConsole.MarkupLine($"[red]{err}[/]");
            return (null, false);
        }

        AnsiConsole.MarkupLine(
            $"[grey]conversa {convId[..8]} — eventos live abaixo[/]");
        return (convId, false);
    }

    /// <summary>
    /// Despacha uma linha de input do prompt (>). Retorna
    /// (novoConvId, sairDoLoop).
    /// </summary>
    private static async Task<(string? ConvId, bool Quit)> DispatchAsync(
        CloudApiClient api, TuiKeymap keys, string input,
        string convId, CancellationToken ct)
    {
        switch (keys.Resolve(input).Trim())
        {
            case "/exit" or "/quit":
                return (convId, true);
            case "/new":
                return (null, false);
            case "/status":
                var c = await api.GetConversationAsync(convId, ct);
                AnsiConsole.MarkupLine(c is { } x
                    ? $"[yellow]{x.GetProperty("execution_status").GetString()}[/]"
                    : "[red]not found[/]");
                return (convId, false);
            case "/list":
                await ShowListAsync(api, ct);
                return (convId, false);
            case "/help":
                ShowHelp();
                return (convId, false);
            case "":
                return (convId, false);
            default:
                await api.SendMessageAsync(convId, input, ct);
                return (convId, false);
        }
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