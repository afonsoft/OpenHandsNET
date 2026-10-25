using OpenHandsNET.Cli.Services;

using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenHandsNET.Cli.Commands;

/// <summary><c>openhandsnet tickets status</c> — connections da org.</summary>
public sealed class TicketsStatusCommand
    : AsyncCommand<TicketsStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }

        [CommandOption("--org <ORG_ID>")]
        public string? OrgId { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: cancellationToken);
        var orgId = settings.OrgId
            ?? await ScmStatusCommand.ResolveOrgAsync(api, cancellationToken);
        var conns = await api.ListTicketConnectionsAsync(orgId, cancellationToken);

        var table = new Table().AddColumns(
            "KIND", "API URL", "WORKSPACE", "ID");
        if (conns.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var c in conns.EnumerateArray())
            {
                table.AddRow(
                    c.TryGetProperty("kind", out var k)
                        ? k.GetString() ?? "" : "",
                    c.TryGetProperty("api_url", out var u)
                        ? u.GetString() ?? "" : "",
                    c.TryGetProperty("workspace_id", out var w)
                        ? w.GetString() ?? "" : "",
                    c.GetProperty("id").GetString()![..8]);
            }
        }
        AnsiConsole.Write(table);
        return 0;
    }
}

/// <summary><c>openhandsnet tickets test --connection &lt;id&gt;</c>.</summary>
public sealed class TicketsTestCommand
    : AsyncCommand<TicketsTestCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--connection <ID>")]
        public required string ConnectionId { get; init; }

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: cancellationToken);
        var diag = await api.TestTicketConnectionAsync(
            settings.ConnectionId, cancellationToken);
        var ok = diag.ValueKind == System.Text.Json.JsonValueKind.Object
            && diag.TryGetProperty("ok", out var o)
            && o.GetBoolean();
        AnsiConsole.MarkupLine(
            ok ? "[green]OK[/] — tracker alcançado."
               : "[red]FALHOU[/]");
        return ok ? 0 : 1;
    }
}

/// <summary>
/// <c>openhandsnet tickets run --connection &lt;id&gt; --issue KEY</c> —
/// dispara uma sessão com o contexto do ticket (sem webhook público).
/// </summary>
public sealed class TicketsRunCommand
    : AsyncCommand<TicketsRunCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--connection <ID>")]
        public required string ConnectionId { get; init; }

        [CommandOption("--issue <KEY>")]
        public required string IssueKey { get; init; }

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: cancellationToken);
        var r = await api.RunTicketAsync(
            settings.ConnectionId, settings.IssueKey, cancellationToken);
        var ok = r.ValueKind == System.Text.Json.JsonValueKind.Object
            && r.TryGetProperty("result", out var res)
            && res.GetString() == "processed";
        AnsiConsole.MarkupLine(ok
            ? $"[green]Start-task disparado[/] p/ {settings.IssueKey}."
            : "[red]Falhou[/] — veja logs do servidor.");
        return ok ? 0 : 1;
    }
}