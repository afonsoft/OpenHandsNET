using Sandcastle.Cli.Services;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Sandcastle.Cli.Commands;

/// <summary><c>sandcastle logout</c> — remove credencial do servidor.</summary>
public sealed class LogoutCommand : AsyncCommand<LogoutCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var url = settings.ServerUrl
            ?? Environment.GetEnvironmentVariable("SANDCASTLE_SERVER_URL")
            ?? CliDefaults.DefaultServerUrl;
        var removed = await new CredentialStore().DeleteAsync(url, cancellationToken);
        AnsiConsole.MarkupLine(removed
            ? $"[green]✓[/] credencial de [bold]{url}[/] removida"
            : $"[grey]nenhuma credencial para {url}[/]");
        return 0;
    }
}