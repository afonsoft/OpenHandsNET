using Spectre.Console;
using Spectre.Console.Cli;
using OpenHandsNET.Cli.Services;

namespace OpenHandsNET.Cli.Commands;

/// <summary><c>openhandsnet scm status</c> — connections da org.</summary>
public sealed class ScmStatusCommand : AsyncCommand<ScmStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }

        [CommandOption("--org <ORG_ID>")]
        public string? OrgId { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: ct);
        var orgId = settings.OrgId ?? await ResolveOrgAsync(api, ct);
        var conns = await api.ListScmConnectionsAsync(orgId, ct);

        var table = new Table().AddColumns(
            "KIND", "APP", "INSTALLATION", "REPOS", "ID");
        if (conns.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var c in conns.EnumerateArray())
            {
                table.AddRow(
                    c.TryGetProperty("kind", out var k)
                        ? k.GetString() ?? "" : "",
                    c.TryGetProperty("app_slug", out var a)
                        ? a.GetString() ?? "" : "",
                    c.TryGetProperty("installation_id", out var i)
                        ? i.GetString() ?? "" : "",
                    c.TryGetProperty("repos", out var r)
                        ? string.Join(",", r.EnumerateArray()
                            .Select(x => x.GetString())) : "",
                    c.GetProperty("id").GetString()![..8]);
            }
        }
        AnsiConsole.Write(table);
        return 0;
    }

    internal static async Task<string> ResolveOrgAsync(
        CloudApiClient api, CancellationToken ct)
    {
        var orgs = await api.ListOrgsAsync(ct);
        if (orgs.ValueKind == System.Text.Json.JsonValueKind.Array
            && orgs.GetArrayLength() == 1)
        {
            return orgs[0].GetProperty("id").GetString()!;
        }
        return AnsiConsole.Prompt(new TextPrompt<string>("Org id:")
            .ValidationErrorMessage("required"));
    }
}

/// <summary>
/// <c>openhandsnet scm connect</c> — imprime o manifest do GitHub App
/// (POST /scm/github/manifest). O callback completa a conexão.
/// </summary>
public sealed class ScmConnectCommand : AsyncCommand<ScmConnectCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }

        [CommandOption("--redirect-uri <URI>")]
        public string? RedirectUri { get; init; }

        [CommandOption("--app-name <NAME>")]
        public string? AppName { get; init; }

        [CommandOption("--kind <KIND>")]
        public string Kind { get; init; } = "github";

        [CommandOption("--url <URL>")]
        public string? ApiUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: ct);
        if (settings.Kind == "github")
        {
            var redirect = settings.RedirectUri
                ?? $"{api.BaseUrl}/api/v1/scm/github/callback";
            var manifest = await api.BuildScmManifestAsync(
                redirect, settings.AppName, ct);
            AnsiConsole.MarkupLine(
                "[bold]GitHub App manifest[/] — crie o app em " +
                "<https://github.com/settings/apps/new> (método manifest) e " +
                "conclua o callback:\n");
            AnsiConsole.WriteLine(manifest.GetRawText());
            return 0;
        }

        // SPEC-53: gitlab/bitbucket/azuredevops — registra via
        // POST /orgs/{org}/scm-connections (PAT/token como secret ref).
        AnsiConsole.MarkupLine(
            $"[bold]{settings.Kind}[/] — registre a connection via API:\n" +
            "  POST /api/v1/orgs/{org_id}/scm-connections\n" +
            "  { \"kind\": \"" + settings.Kind + "\",\n" +
            "    \"api_url\": \"" + (settings.ApiUrl ?? "<base-url>") + "\",\n" +
            "    \"pem_secret_ref\": \"<secret-ref-do-token>\",\n" +
            "    \"webhook_secret_ref\": \"<secret-ref-do-hook>\",\n" +
            "    \"repos\": [\"group/repo\"] }\n" +
            "Webhook: POST /api/v1/webhooks/scm/" + settings.Kind + "\n" +
            "Diagnóstico: POST /api/v1/scm/connections/{id}/test");
        return 0;
    }
}

/// <summary><c>openhandsnet scm pr create &lt;conversation&gt;</c>.</summary>
public sealed class ScmPrCreateCommand
    : AsyncCommand<ScmPrCreateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<CONVERSATION>")]
        public required string ConversationId { get; init; }

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }

        [CommandOption("--title <TITLE>")]
        public string? Title { get; init; }

        [CommandOption("--body <BODY>")]
        public string? Body { get; init; }

        [CommandOption("--base <BRANCH>")]
        public string? BaseBranch { get; init; }

        [CommandOption("--draft")]
        public bool Draft { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: ct);
        var pr = await api.CreatePullRequestAsync(
            settings.ConversationId,
            settings.Title ?? "OpenHandsNET changes",
            settings.Body ?? "", settings.BaseBranch,
            settings.Draft, ct);
        AnsiConsole.MarkupLine(
            $"[green]PR #{pr.GetProperty("number")}[/] " +
            Markup.Escape(pr.GetProperty("url").GetString() ?? ""));
        return 0;
    }
}
