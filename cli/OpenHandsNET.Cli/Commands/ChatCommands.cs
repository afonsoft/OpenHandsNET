using OpenHandsNET.Cli.Services;

using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenHandsNET.Cli.Commands;

/// <summary><c>openhandsnet chat status</c> — chat connections da org.</summary>
public sealed class ChatStatusCommand : AsyncCommand<ChatStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }

        [CommandOption("--org <ORG_ID>")]
        public string? OrgId { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken ct)
    {
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: ct);
        var orgId = settings.OrgId
            ?? await ScmStatusCommand.ResolveOrgAsync(api, ct);
        var conns = await api.ListChatConnectionsAsync(orgId, ct);

        var table = new Table().AddColumns(
            "KIND", "TEAM", "BOT USER", "CHANNELS", "ID");
        if (conns.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var c in conns.EnumerateArray())
            {
                table.AddRow(
                    c.TryGetProperty("kind", out var k)
                        ? k.GetString() ?? "" : "",
                    c.TryGetProperty("team_id", out var t)
                        ? t.GetString() ?? "" : "",
                    c.TryGetProperty("bot_user_id", out var b)
                        ? b.GetString() ?? "" : "",
                    c.TryGetProperty("channels", out var ch)
                        && ch.ValueKind
                            == System.Text.Json.JsonValueKind.Array
                        ? ch.GetArrayLength().ToString() : "0",
                    c.GetProperty("id").GetString()![..8]);
            }
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            "\nWebhook URLs para o Slack app:\n" +
            "  Events:       POST /api/v1/webhooks/chat/slack/events\n" +
            "  Commands:     POST /api/v1/webhooks/chat/slack/commands\n" +
            "  Interactions: POST /api/v1/webhooks/chat/slack/interactions");
        return 0;
    }
}

/// <summary>
/// <c>openhandsnet chat test --connection &lt;id&gt;</c> — diagnóstico
/// (resolve bot token no secret store e chama auth.test no Slack).
/// </summary>
public sealed class ChatTestCommand : AsyncCommand<ChatTestCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--connection <ID>")]
        public required string ConnectionId { get; init; }

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken ct)
    {
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: ct);
        var diag = await api.TestChatConnectionAsync(
            settings.ConnectionId, ct);
        var ok = diag.ValueKind == System.Text.Json.JsonValueKind.Object
            && diag.TryGetProperty("ok", out var o)
            && o.GetBoolean();
        var detail = diag.ValueKind == System.Text.Json.JsonValueKind.Object
            && diag.TryGetProperty("detail", out var d)
                ? Markup.Escape(d.GetString() ?? "")
                : "sem detalhe";
        AnsiConsole.MarkupLine(
            ok ? "[green]OK[/] — connection alcançou o Slack."
               : $"[red]FALHOU[/] — {detail}");
        return ok ? 0 : 1;
    }
}