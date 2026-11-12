using System.Text.Json;

using Sandcastle.Cli.Services;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Sandcastle.Cli.Commands;

/// <summary>
/// <c>sandcastle marketplace list</c> — SPEC-148 RF-004: catálogo
/// instalável (kind/versão/installed por escopo). <c>--installed</c>
/// lista as linhas de <c>installed_skills</c> visíveis ao caller.
/// </summary>
public sealed class MarketplaceListCommand
    : AsyncCommand<MarketplaceListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--kind <KIND>")]
        public string? Kind { get; init; }

        [CommandOption("-q|--query <QUERY>")]
        public string? Query { get; init; }

        [CommandOption("--installed")]
        public bool Installed { get; init; }

        [CommandOption("--scope <SCOPE>")]
        public string? Scope { get; init; }

        [CommandOption("--json")]
        public bool Json { get; init; }

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(),
            ct: cancellationToken);
        if (settings.Installed)
        {
            var doc = await api.GetMarketplaceInstalledAsync(
                settings.Scope, cancellationToken);
            if (settings.Json)
            {
                Console.WriteLine(doc.GetRawText());
                return 0;
            }

            var table = new Table().AddColumns(
                "ID", "ENTRY", "SCOPE", "VERSION", "ENABLED", "UPDATE");
            if (doc.TryGetProperty("items", out var items))
            {
                foreach (var i in items.EnumerateArray())
                {
                    table.AddRow(
                        i.GetProperty("id").GetString()![..8],
                        i.GetProperty("entry_id").GetString() ?? "",
                        i.GetProperty("scope").GetString() ?? "",
                        i.GetProperty("version").GetString() ?? "",
                        i.GetProperty("enabled").GetBoolean()
                            ? "yes" : "no",
                        i.TryGetProperty("update_available", out var u)
                            && u.GetBoolean() ? "available" : "");
                }
            }

            AnsiConsole.Write(table);
            return 0;
        }

        var entries = await api.GetMarketplaceEntriesAsync(
            settings.Query, settings.Kind, cancellationToken);
        if (settings.Json)
        {
            Console.WriteLine(entries.GetRawText());
            return 0;
        }

        var t = new Table().AddColumns(
            "ID", "KIND", "VERSION", "NAME", "INSTALLED");
        if (entries.TryGetProperty("items", out var arr))
        {
            foreach (var e in arr.EnumerateArray())
            {
                var scopes = e.TryGetProperty("installed", out var inst)
                    ? string.Join(",", inst.EnumerateArray()
                        .Select(x => x.GetProperty("scope").GetString()))
                    : "";
                t.AddRow(
                    e.GetProperty("id").GetString() ?? "",
                    e.GetProperty("kind").GetString() ?? "",
                    e.GetProperty("version").GetString() ?? "",
                    e.GetProperty("name").GetString() ?? "",
                    scopes);
            }
        }

        AnsiConsole.Write(t);
        return 0;
    }
}

/// <summary>
/// <c>sandcastle marketplace install &lt;entry&gt;</c> — instala um
/// entry no escopo pedido (default user). Entries com
/// <c>permissions_required</c> exigem <c>--yes</c> (aceite explícito —
/// nunca implícito, SPEC-148 §9).
/// </summary>
public sealed class MarketplaceInstallCommand
    : AsyncCommand<MarketplaceInstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ENTRY>")]
        public required string EntryId { get; init; }

        [CommandOption("--scope <SCOPE>")]
        public string Scope { get; init; } = "user";

        [CommandOption("--org <ID>")]
        public string? OrgId { get; init; }

        [CommandOption("--version <V>")]
        public string? Version { get; init; }

        [CommandOption("--yes")]
        public bool AcceptPermissions { get; init; }

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Scope is not ("user" or "org" or "system"))
        {
            AnsiConsole.MarkupLine(
                "[red]--scope deve ser user|org|system[/]");
            return 1;
        }

        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(),
            ct: cancellationToken);

        // permissions_required declaradas — avisa antes do install.
        var detail = await api.GetMarketplaceEntryAsync(
            settings.EntryId, cancellationToken);
        var perms = detail is { } d
            && d.TryGetProperty("permissions_required", out var p)
            && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(x => x.GetString())
                    .Where(x => x is not null).ToList()
                : [];
        if (perms.Count > 0 && !settings.AcceptPermissions)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]entry declara permissions_required: "
                + $"{string.Join(", ", perms)}[/]");
            AnsiConsole.MarkupLine(
                "[yellow]repita com --yes para aceitar explicitamente[/]");
            return 2;
        }

        var (status, body) = await api.InstallMarketplaceAsync(
            settings.EntryId, settings.Scope, settings.OrgId,
            settings.Version, settings.AcceptPermissions,
            cancellationToken);
        if (status is >= 200 and < 300)
        {
            var scopeId = body.TryGetProperty("scope_id", out var s)
                && s.ValueKind == JsonValueKind.String
                    ? s.GetString() : null;
            AnsiConsole.MarkupLine(
                $"[green]installed[/] {settings.EntryId} "
                + $"@ {settings.Scope}"
                + (scopeId is not null ? $" ({scopeId})" : ""));
            return 0;
        }

        var detailMsg = body.TryGetProperty("detail", out var dm)
            ? dm.GetString() : body.GetRawText()[..Math.Min(
                200, body.GetRawText().Length)];
        AnsiConsole.MarkupLine(
            $"[red]install failed ({status})[/] {Markup.Escape(detailMsg ?? "")}");
        return status switch
        {
            403 => 3,
            409 => 4,
            422 => 5,
            502 => 6,
            _ => 1,
        };
    }
}

/// <summary>
/// Base para remove/upgrade — resolve o argumento (installed id GUID
/// ou entry id → lookup em <c>/installed</c>, escopo via flags).
/// </summary>
public abstract class MarketplaceRowCommand<T>
    : AsyncCommand<T> where T : MarketplaceRowCommand<T>.RowSettings
{
    public class RowSettings : CommandSettings
    {
        [CommandArgument(0, "<ID|ENTRY>")]
        public required string Id { get; init; }

        [CommandOption("--scope <SCOPE>")]
        public string? Scope { get; init; }

        [CommandOption("--org <ID>")]
        public string? OrgId { get; init; }

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    /// <summary>
    /// Resolve o installed-id: GUID direto, ou entry-id → linha em
    /// /installed (escopo filtra; múltiplas linhas = erro pedindo o id).
    /// </summary>
    protected static async Task<Guid?> ResolveRowAsync(
        CloudApiClient api, RowSettings s, CancellationToken ct)
    {
        if (Guid.TryParse(s.Id, out var gid))
        {
            return gid;
        }

        var doc = await api.GetMarketplaceInstalledAsync(s.Scope, ct);
        if (!doc.TryGetProperty("items", out var items))
        {
            return null;
        }

        var matches = items.EnumerateArray()
            .Where(i => i.GetProperty("entry_id").GetString()
                ?.Equals(s.Id, StringComparison.OrdinalIgnoreCase) == true)
            .Where(i => s.OrgId is null
                || i.TryGetProperty("scope_id", out var sid)
                    && sid.GetString() == s.OrgId)
            .ToList();
        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[red]nenhum install de '{s.Id}' visível[/]");
            return null;
        }

        if (matches.Count > 1)
        {
            AnsiConsole.MarkupLine(
                "[red]múltiplos installs — use o installed id:[/]");
            foreach (var m in matches)
            {
                AnsiConsole.MarkupLine(
                    $"  {m.GetProperty("id").GetString()} "
                    + $"({m.GetProperty("scope").GetString()})");
            }

            return null;
        }

        return matches[0].GetProperty("id").GetGuid();
    }
}

/// <summary><c>marketplace remove</c> — remove + desmaterializa.</summary>
public sealed class MarketplaceRemoveCommand
    : MarketplaceRowCommand<MarketplaceRemoveCommand.Settings>
{
    public sealed class Settings : RowSettings;

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(),
            ct: cancellationToken);
        if (await ResolveRowAsync(api, settings, cancellationToken)
            is not { } id)
        {
            return 1;
        }

        var status = await api.RemoveMarketplaceAsync(id, cancellationToken);
        if (status is >= 200 and < 300)
        {
            AnsiConsole.MarkupLine($"[green]removed[/] {settings.Id}");
            return 0;
        }

        AnsiConsole.MarkupLine($"[red]remove failed ({status})[/]");
        return 1;
    }
}

/// <summary><c>marketplace upgrade</c> — bump para a versão do registry.</summary>
public sealed class MarketplaceUpgradeCommand
    : MarketplaceRowCommand<MarketplaceUpgradeCommand.Settings>
{
    public sealed class Settings : RowSettings;

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(),
            ct: cancellationToken);
        if (await ResolveRowAsync(api, settings, cancellationToken)
            is not { } id)
        {
            return 1;
        }

        var status = await api.UpgradeMarketplaceAsync(id, cancellationToken);
        if (status is >= 200 and < 300)
        {
            AnsiConsole.MarkupLine($"[green]upgraded[/] {settings.Id}");
            return 0;
        }

        AnsiConsole.MarkupLine(
            status == 409
                ? "[yellow]já está na versão mais recente[/]"
                : $"[red]upgrade failed ({status})[/]");
        return status == 409 ? 0 : 1;
    }
}