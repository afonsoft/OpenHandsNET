using Spectre.Console;
using Spectre.Console.Cli;
using OpenHandsNET.Cli.Services;

namespace OpenHandsNET.Cli.Commands;

/// <summary>
/// <c>openhandsnet service-users</c> — SPEC-57: gerencia service users
/// e chaves <c>svc_*</c> escopadas.
/// </summary>
public sealed class ServiceUsersListCommand
    : AsyncCommand<ServiceUsersListCommand.Settings>
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
        var orgId = settings.OrgId
            ?? await ScmStatusCommand.ResolveOrgAsync(api, ct);
        var users = await api.ListServiceUsersAsync(orgId, ct);

        var table = new Table().AddColumns("NAME", "ROLE", "STATUS", "ID");
        if (users.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var u in users.EnumerateArray())
            {
                var disabled = u.TryGetProperty("disabled_at", out var da)
                    && da.ValueKind == System.Text.Json.JsonValueKind.String;
                table.AddRow(
                    u.TryGetProperty("name", out var n)
                        ? n.GetString() ?? "" : "",
                    u.TryGetProperty("role", out var r)
                        ? r.GetString() ?? "" : "",
                    disabled ? "disabled" : "active",
                    u.GetProperty("id").GetString()![..8]);
            }
        }
        AnsiConsole.Write(table);
        return 0;
    }
}

/// <summary><c>service-users create --name X --role member</c>.</summary>
public sealed class ServiceUsersCreateCommand
    : AsyncCommand<ServiceUsersCreateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--name <NAME>")]
        public required string Name { get; init; }

        [CommandOption("--role <ROLE>")]
        public string Role { get; init; } = "member";

        [CommandOption("--org <ORG_ID>")]
        public string? OrgId { get; init; }

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: ct);
        var orgId = settings.OrgId
            ?? await ScmStatusCommand.ResolveOrgAsync(api, ct);
        var ok = await api.CreateServiceUserAsync(
            orgId, settings.Name, settings.Role, ct);
        AnsiConsole.MarkupLine(ok
            ? $"[green]Criado[/] service user {settings.Name}."
            : "[red]Falhou[/] — veja logs do servidor.");
        return ok ? 0 : 1;
    }
}

/// <summary>
/// <c>service-users key --id &lt;svc&gt; --scopes "conversations:write"</c>
/// — imprime o plaintext uma única vez.
/// </summary>
public sealed class ServiceUsersKeyCommand
    : AsyncCommand<ServiceUsersKeyCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--id <SVC_ID>")]
        public required string ServiceUserId { get; init; }

        [CommandOption("--scopes <SCOPES>")]
        public required string Scopes { get; init; }

        [CommandOption("--org <ORG_ID>")]
        public string? OrgId { get; init; }

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: ct);
        var orgId = settings.OrgId
            ?? await ScmStatusCommand.ResolveOrgAsync(api, ct);
        var key = await api.CreateServiceUserKeyAsync(
            orgId, settings.ServiceUserId,
            settings.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            ct);
        if (key is null)
        {
            AnsiConsole.MarkupLine("[red]Falhou[/] — escopo desconhecido?");
            return 1;
        }
        AnsiConsole.MarkupLine(
            "[yellow]Chave (exibida uma vez — guarde agora):[/]");
        AnsiConsole.WriteLine(key);
        return 0;
    }
}
