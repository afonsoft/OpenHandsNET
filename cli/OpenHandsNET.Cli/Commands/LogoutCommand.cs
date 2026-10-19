using OpenHandsNET.Cli.Services;

using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenHandsNET.Cli.Commands;

/// <summary><c>openhandsnet logout</c> — remove credencial do servidor.</summary>
public sealed class LogoutCommand : AsyncCommand<LogoutCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var url = settings.ServerUrl
            ?? Environment.GetEnvironmentVariable("OPENHANDSNET_SERVER_URL")
            ?? "http://localhost:8080";
        var removed = await new CredentialStore().DeleteAsync(url, ct);
        AnsiConsole.MarkupLine(removed
            ? $"[green]✓[/] credencial de [bold]{url}[/] removida"
            : $"[grey]nenhuma credencial para {url}[/]");
        return 0;
    }
}