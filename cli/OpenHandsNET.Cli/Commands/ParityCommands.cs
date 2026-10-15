using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using OpenHandsNET.Cli.Services;

namespace OpenHandsNET.Cli.Commands;

/// <summary>
/// <c>openhandsnet models</c> — SPEC-31 RF-001: catálogo OmniRoute
/// (SPEC-23): provider / model / context window / pricing.
/// </summary>
public sealed class ModelsCommand : AsyncCommand<ModelsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }

        [CommandOption("--json")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(),
            ct: CancellationToken.None);
        var catalog = await api.GetModelsAsync();
        if (settings.Json)
        {
            Console.WriteLine(catalog.GetRawText());
            return 0;
        }

        var table = new Table()
            .AddColumns("PROVIDER", "MODEL", "WINDOW", "$/M IN", "$/M OUT");
        if (catalog.TryGetProperty("models", out var models))
        {
            foreach (var m in models.EnumerateArray())
            {
                var pricing = m.TryGetProperty("pricing", out var p)
                    && p.ValueKind == JsonValueKind.Object;
                table.AddRow(
                    m.TryGetProperty("provider", out var pr)
                        ? pr.GetString() ?? "" : "",
                    m.GetProperty("id").GetString() ?? "",
                    m.TryGetProperty("context_window", out var w)
                        && w.ValueKind == JsonValueKind.Number
                        ? w.GetInt64().ToString("N0") : "?",
                    pricing && p.TryGetProperty("input_per_million", out var i)
                        && i.ValueKind == JsonValueKind.Number
                        ? i.GetDecimal().ToString("0.###") : "-",
                    pricing && p.TryGetProperty("output_per_million", out var o)
                        && o.ValueKind == JsonValueKind.Number
                        ? o.GetDecimal().ToString("0.###") : "-");
            }
        }

        AnsiConsole.Write(table);
        if (catalog.TryGetProperty("default", out var d))
        {
            AnsiConsole.MarkupLine($"[grey]default: {d.GetString()}[/]");
        }

        return 0;
    }
}

/// <summary>
/// <c>openhandsnet stats</c> — SPEC-31 RF-001: uso agregado — contagem de
/// conversas + usage da org (quando resolvível/permissionada).
/// </summary>
public sealed class StatsCommand : AsyncCommand<StatsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }

        [CommandOption("--org <ID>")]
        public string? OrgId { get; init; }

        [CommandOption("--json")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: ct);
        var conversations = await api.CountConversationsAsync(ct);

        JsonElement? usage = null;
        var orgId = settings.OrgId;
        if (orgId is null)
        {
            // Resolve a primeira org do usuário (best-effort; OSS sem orgs
            // cai no ramo de only-conversations).
            var orgs = await api.ListOrgsAsync(ct);
            if (orgs.ValueKind == JsonValueKind.Array
                && orgs.GetArrayLength() > 0)
            {
                orgId = orgs[0].TryGetProperty("id", out var i)
                    ? i.GetString() : null;
            }
            else if (orgs.ValueKind == JsonValueKind.Object
                && orgs.TryGetProperty("items", out var items)
                && items.GetArrayLength() > 0)
            {
                orgId = items[0].TryGetProperty("id", out var i)
                    ? i.GetString() : null;
            }
        }

        if (orgId is not null && Guid.TryParse(orgId, out var og))
        {
            usage = await api.GetOrgUsageAsync(og, ct);
        }

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                conversations,
                org_id = orgId,
                usage = usage is { } uv
                    ? (object)uv : null,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var table = new Table().AddColumns("METRIC", "VALUE");
        table.AddRow("conversations", conversations.ToString());
        if (usage is { } u)
        {
            foreach (var prop in u.EnumerateObject())
            {
                table.AddRow(Markup.Escape(prop.Name),
                    Markup.Escape(prop.Value.ToString()));
            }
        }
        else
        {
            table.AddRow("[grey]org usage[/]",
                "[grey]unavailable (no org / no billing perm)[/]");
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

/// <summary>
/// <c>openhandsnet skills &lt;conversation&gt;</c> — SPEC-31 RF-001: tools
/// efetivas da conversa (SPEC-25/29); <c>--all</c> lista tudo, default
/// filtra skills/MCP.
/// </summary>
public sealed class SkillsCommand : AsyncCommand<SkillsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<CONVERSATION>")]
        public required string ConversationId { get; init; }

        [CommandOption("--all")]
        public bool All { get; init; }

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(),
            ct: CancellationToken.None);
        var resp = await api.GetToolsAsync(settings.ConversationId);
        var table = new Table().AddColumns("TOOL", "SOURCE");
        if (resp.TryGetProperty("tools", out var tools))
        {
            foreach (var t in tools.EnumerateArray())
            {
                var name = t.GetProperty("name").GetString() ?? "";
                var source = name.StartsWith("mcp__", StringComparison.Ordinal)
                    ? "mcp"
                    : name is "invoke_skill" or "task" ? "skills"
                    : "builtin";
                if (!settings.All && source == "builtin")
                {
                    continue;
                }

                table.AddRow(name, source);
            }
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

/// <summary>
/// <c>openhandsnet export &lt;conversation&gt;</c> — SPEC-31 RF-001: dump
/// JSONL de eventos duráveis no stdout ou <c>--out</c> (secrets redigidos
/// pelo mesmo SecretRedactor do runner).
/// </summary>
public sealed class ExportCommand : AsyncCommand<ExportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<CONVERSATION>")]
        public required string ConversationId { get; init; }

        [CommandOption("--out <PATH>")]
        public string? Out { get; init; }

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: ct);
        await using var file = settings.Out is { } path
            ? new StreamWriter(path) : null;
        var count = 0;
        await foreach (var e in api.ExportEventsAsync(
            settings.ConversationId, ct))
        {
            var line = Redact(e.GetRawText());
            if (file is not null)
            {
                await file.WriteLineAsync(line);
            }
            else
            {
                Console.WriteLine(line);
            }

            count++;
        }

        if (file is not null)
        {
            AnsiConsole.MarkupLine(
                $"[green]✓[/] {count} events → {Markup.Escape(settings.Out!)}");
        }

        return 0;
    }

    /// <summary>Máscara de placeholders <c>{{secret:NAME}}</c>.</summary>
    internal static string Redact(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text, @"\{\{secret:[^}]+\}\}", "***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

/// <summary>
/// <c>openhandsnet conversations resume &lt;id&gt;</c> — SPEC-31 RF-001:
/// replay do tail + loop de input (equivalente ao claude-code --resume).
/// </summary>
public sealed class ResumeCommand : AsyncCommand<ResumeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("--tail <N>")]
        public int Tail { get; init; } = 20;

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: ct);
        var conv = await api.GetConversationAsync(settings.Id, ct);
        if (conv is null)
        {
            AnsiConsole.MarkupLine("[red]conversation not found[/]");
            return 1;
        }

        // Replay tail.
        var page = await api.GetEventsAsync(settings.Id, ct: ct);
        if (page.TryGetProperty("items", out var items))
        {
            var last = items.EnumerateArray().TakeLast(settings.Tail);
            foreach (var e in last)
            {
                Tui.EventRenderer.Write(e);
            }
        }

        AnsiConsole.MarkupLine(
            $"[grey]resumed {settings.Id[..8]} — /exit sai[/]");
        while (!ct.IsCancellationRequested)
        {
            var input = AnsiConsole.Ask<string>("[green]>[/]");
            if (input.Trim() is "/exit" or "/quit")
            {
                return 0;
            }

            if (input.Trim().Length == 0)
            {
                continue;
            }

            await api.SendMessageAsync(settings.Id, input, ct);
            await Task.Delay(800, ct);
            var poll = await api.GetEventsAsync(settings.Id, ct: ct);
            if (poll.TryGetProperty("items", out var fresh))
            {
                foreach (var e in fresh.EnumerateArray().TakeLast(5))
                {
                    Tui.EventRenderer.Write(e);
                }
            }
        }

        return 0;
    }
}

/// <summary>
/// <c>openhandsnet attach --url &lt;u&gt;</c> — SPEC-31 RF-001: TUI contra
/// backend remoto (mesma credencial do login).
/// </summary>
public sealed class AttachCommand : AsyncCommand<AttachCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--url <URL>")]
        public required string Url { get; init; }

        [CommandOption("-c|--conversation <ID>")]
        public string? ConversationId { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings) =>
        await new TuiCommand().ExecuteAsync(context,
            new TuiCommand.Settings
            {
                ServerUrl = settings.Url,
                ConversationId = settings.ConversationId,
            });
}

/// <summary><c>openhandsnet mcp list</c> — SPEC-31 RF-001.</summary>
public sealed class McpListCommand : AsyncCommand<McpListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(),
            ct: CancellationToken.None);
        var servers = await api.GetMcpServersAsync();
        var table = new Table().AddColumns("NAME", "TYPE", "ENDPOINT");
        if (servers.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in servers.EnumerateArray())
            {
                table.AddRow(
                    s.TryGetProperty("name", out var n)
                        ? n.GetString() ?? "" : "",
                    s.TryGetProperty("transport", out var t)
                        ? t.GetString() ?? ""
                        : s.TryGetProperty("type", out var t2)
                            ? t2.GetString() ?? "" : "",
                    (s.TryGetProperty("url", out var u) ? u.GetString()
                        : s.TryGetProperty("command", out var c)
                            ? c.GetString() : null) ?? "");
            }
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

/// <summary>
/// <c>openhandsnet mcp test &lt;name&gt;</c> — SPEC-31 RF-001: probe de um
/// server configurado via <c>POST /mcp/test</c>.
/// </summary>
public sealed class McpTestCommand : AsyncCommand<McpTestCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<NAME>")]
        public required string Name { get; init; }

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: ct);
        var servers = await api.GetMcpServersAsync(ct);
        JsonElement? target = null;
        if (servers.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in servers.EnumerateArray())
            {
                if (s.TryGetProperty("name", out var n)
                    && n.GetString() == settings.Name)
                {
                    target = s;
                    break;
                }
            }
        }

        if (target is null)
        {
            AnsiConsole.MarkupLine($"[red]server '{settings.Name}' not found[/]");
            return 1;
        }

        var probe = await api.ProbeMcpAsync(target.Value, ct);
        var ok = probe.TryGetProperty("ok", out var o) && o.GetBoolean();
        var detail = probe.TryGetProperty("error", out var e)
            ? e.GetString()
            : probe.TryGetProperty("tools", out var t)
                ? $"{t.GetArrayLength()} tools" : "connected";
        AnsiConsole.MarkupLine(ok
            ? $"[green]✓[/] {settings.Name}: {detail}"
            : $"[red]✗[/] {settings.Name}: {detail}");
        return ok ? 0 : 1;
    }
}
