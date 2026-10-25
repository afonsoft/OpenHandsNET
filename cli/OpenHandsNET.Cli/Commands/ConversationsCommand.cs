using OpenHandsNET.Cli.Services;

using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenHandsNET.Cli.Commands;

/// <summary><c>openhandsnet conversations list|status|delete</c>.</summary>
public sealed class ConversationsListCommand : AsyncCommand<ConversationsListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: cancellationToken);
        var page = await api.ListConversationsAsync(cancellationToken);
        var table = new Table().AddColumns("STATUS", "TITLE", "REPO", "ID");
        if (page.TryGetProperty("items", out var items))
        {
            foreach (var c in items.EnumerateArray())
            {
                table.AddRow(
                    c.TryGetProperty("execution_status", out var s)
                        ? s.GetString() ?? "" : "",
                    c.TryGetProperty("title", out var t)
                        ? t.GetString() ?? "untitled" : "untitled",
                    c.TryGetProperty("selected_repository", out var r)
                        ? r.GetString() ?? "" : "",
                    c.GetProperty("id").GetString()![..8]);
            }
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

/// <summary><c>openhandsnet conversations status &lt;id&gt;</c>.</summary>
public sealed class ConversationsStatusCommand : AsyncCommand<ConversationsStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: cancellationToken);
        var conv = await api.GetConversationAsync(settings.Id, cancellationToken);
        if (conv is null)
        {
            AnsiConsole.MarkupLine("[red]conversation not found[/]");
            return 1;
        }

        var c = conv.Value;
        var title = c.TryGetProperty("title", out var t)
            ? t.GetString() : "?";
        AnsiConsole.MarkupLine($"[bold]{title}[/] ({settings.Id[..8]})");
        AnsiConsole.MarkupLine(
            $"  status: [yellow]{c.GetProperty("execution_status").GetString()}[/]");
        return 0;
    }
}

/// <summary><c>openhandsnet conversations delete &lt;id&gt;</c>.</summary>
public sealed class ConversationsDeleteCommand : AsyncCommand<ConversationsDeleteCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: cancellationToken);
        await api.DeleteConversationAsync(settings.Id, cancellationToken);
        AnsiConsole.MarkupLine($"[green]✓[/] deleted {settings.Id[..8]}");
        return 0;
    }
}