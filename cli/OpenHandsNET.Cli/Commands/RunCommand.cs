using Spectre.Console;
using Spectre.Console.Cli;
using OpenHandsNET.Cli.Services;

namespace OpenHandsNET.Cli.Commands;

/// <summary>
/// <c>openhandsnet run</c> — headless CI: cria + aguarda terminal state.
/// <c>--json</c> emite JSON final; exit codes: 0 finished · 2 error · 3 timeout.
/// </summary>
public sealed class RunCommand : AsyncCommand<RunCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-t|--task <TEXT>")]
        public string? Task { get; init; }

        [CommandOption("-f|--file <PATH>")]
        public string? File { get; init; }

        [CommandOption("--repo <REPO>")]
        public string? Repository { get; init; }

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }

        [CommandOption("--json")]
        public bool Json { get; init; }

        [CommandOption("--exit-on-finish")]
        public bool ExitOnFinish { get; init; }

        [CommandOption("--timeout-min <MIN>")]
        public double TimeoutMinutes { get; init; } = 30;

        [CommandOption("--confirm")]
        public bool ConfirmationMode { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var task = settings.Task ?? (settings.File is { } f
            ? await System.IO.File.ReadAllTextAsync(f, ct) : null);
        if (string.IsNullOrWhiteSpace(task))
        {
            Console.Error.WriteLine("task required (-t or -f)");
            return 1;
        }

        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: ct);
        var runner = new RunExecutor(api);

        var created = await api.CreateConversationAsync(
            task, settings.Repository,
            confirmationMode: settings.ConfirmationMode, ct: ct);
        var taskId = created.GetProperty("id").GetString()!;
        var (convId, error) = await runner.WaitReadyAsync(taskId, ct: ct);
        if (convId is null)
        {
            if (settings.Json)
            {
                Console.WriteLine(RunExecutor.ToJson(
                    new RunExecutor.Result(2, null, "ERROR", error)));
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]{error}[/]");
            }

            return 2;
        }

        var result = await runner.WatchAsync(convId,
            TimeSpan.FromMinutes(settings.TimeoutMinutes),
            settings.Json ? null : e => Tui.EventRenderer.Write(e), ct);
        if (settings.Json)
        {
            Console.WriteLine(RunExecutor.ToJson(result));
        }
        else
        {
            AnsiConsole.MarkupLine(result.ExitCode == 0
                ? $"[green]✓ finished[/] {api.BaseUrl}/conversations/{convId}"
                : $"[red]✗ {result.ExecutionStatus}[/] {result.Detail}");
        }

        return result.ExitCode;
    }
}
