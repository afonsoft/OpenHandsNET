using OpenHandsNET.Cli.Services;

using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenHandsNET.Cli.Commands;

/// <summary>
/// <c>openhandsnet cloud -t "task"</c> — cria conversa no servidor e
/// imprime a URL ao READY; <c>--watch</c> anexa o stream até terminal.
/// </summary>
public sealed class CloudCommand : AsyncCommand<CloudCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-t|--task <TEXT>")]
        public string? Task { get; init; }

        [CommandOption("-f|--file <PATH>")]
        public string? File { get; init; }

        [CommandOption("--repo <OWNER_REPO>")]
        public string? Repository { get; init; }

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }

        [CommandOption("--watch")]
        public bool Watch { get; init; }

        [CommandOption("--confirm")]
        public bool ConfirmationMode { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken ct)
    {
        var task = settings.Task;
        if (settings.File is { } file)
        {
            if (!System.IO.File.Exists(file))
            {
                AnsiConsole.MarkupLine($"[red]arquivo não encontrado: {file}[/]");
                return 1;
            }

            task = await System.IO.File.ReadAllTextAsync(file, ct);
        }

        if (string.IsNullOrWhiteSpace(task))
        {
            AnsiConsole.MarkupLine("[red]informe -t \"task\" ou -f arquivo[/]");
            return 1;
        }

        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: ct);
        var runner = new RunExecutor(api);

        var convId = await AnsiConsole.Status().StartAsync(
            "creating conversation…", async ctx =>
            {
                var created = await api.CreateConversationAsync(
                    task, settings.Repository,
                    confirmationMode: settings.ConfirmationMode, ct: ct);
                var taskId = created.GetProperty("id").GetString()!;
                var (readyId, error) = await runner.WaitReadyAsync(
                    taskId, s => ctx.Status($"start-task: {s}"), ct);
                if (error is not null)
                {
                    throw new HttpRequestException(error);
                }
                return readyId;
            });

        if (convId is null)
        {
            AnsiConsole.MarkupLine("[red]start-task falhou[/]");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[green]✓[/] {api.BaseUrl}/conversations/{convId}");

        if (settings.Watch)
        {
            var result = await runner.WatchAsync(convId,
                TimeSpan.FromMinutes(30),
                e => Tui.EventRenderer.Write(e), ct);
            return result.ExitCode;
        }

        return 0;
    }
}