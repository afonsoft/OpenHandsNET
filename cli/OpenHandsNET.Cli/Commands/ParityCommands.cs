using System.Text.Json;

using OpenHandsNET.Cli.Services;

using Spectre.Console;
using Spectre.Console.Cli;

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
        CommandContext context, Settings settings, CancellationToken ct)
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
                AddModelRow(table, m);
            }
        }

        AnsiConsole.Write(table);
        if (catalog.TryGetProperty("default", out var d))
        {
            AnsiConsole.MarkupLine($"[grey]default: {d.GetString()}[/]");
        }

        return 0;
    }

    private static void AddModelRow(Table table, JsonElement m)
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
        CommandContext context, Settings settings, CancellationToken ct)
    {
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: ct);
        var conversations = await api.CountConversationsAsync(ct);

        JsonElement? usage = null;
        var orgId = settings.OrgId ?? await ResolveOrgIdAsync(api, ct);
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

    /// <summary>
    /// Resolve a primeira org do usuário (best-effort; OSS sem orgs
    /// devolve null e o fluxo segue só com conversations).
    /// </summary>
    private static async Task<string?> ResolveOrgIdAsync(
        CloudApiClient api, CancellationToken ct)
    {
        var orgs = await api.ListOrgsAsync(ct);
        if (orgs.ValueKind == JsonValueKind.Array
            && orgs.GetArrayLength() > 0)
        {
            return orgs[0].TryGetProperty("id", out var i)
                ? i.GetString() : null;
        }

        if (orgs.ValueKind == JsonValueKind.Object
            && orgs.TryGetProperty("items", out var items)
            && items.GetArrayLength() > 0)
        {
            return items[0].TryGetProperty("id", out var i)
                ? i.GetString() : null;
        }

        return null;
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
        CommandContext context, Settings settings, CancellationToken ct)
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
                var source = name switch
                {
                    _ when name.StartsWith("mcp__", StringComparison.Ordinal)
                        => "mcp",
                    "invoke_skill" or "task" => "skills",
                    _ => "builtin",
                };
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
/// <c>openhandsnet export &lt;conversation&gt;</c> — SPEC-56 RF-004:
/// baixa o zip de trajetória (<c>events.jsonl</c> + <c>metadata.json</c>
/// + <c>workspace.diff</c> + <c>summary.md</c>) via
/// <c>GET /app-conversations/{id}/download</c>. <c>--jsonl</c> mantém o
/// dump legado SPEC-31 no stdout/--out.
/// </summary>
public sealed class ExportCommand : AsyncCommand<ExportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<CONVERSATION>")]
        public required string ConversationId { get; init; }

        [CommandOption("--out <PATH>")]
        public string? Out { get; init; }

        [CommandOption("--jsonl")]
        public bool Jsonl { get; init; }

        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken ct)
    {
        var api = await CloudApiClient.CreateAsync(
            settings.ServerUrl, new CredentialStore(), ct: ct);

        if (settings.Jsonl)
        {
            return await ExportJsonlAsync(api, settings, ct);
        }

        var path = settings.Out
            ?? $"openhandsnet-{settings.ConversationId}.zip";
        using var resp = await api.DownloadTrajectoryAsync(
            settings.ConversationId, ct);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        await src.CopyToAsync(dst, ct);
        AnsiConsole.MarkupLine(
            $"[green]✓[/] trajectory → {Markup.Escape(path)}");
        return 0;
    }

    /// <summary>Dump JSONL legado (SPEC-31 RF-001).</summary>
    private static async Task<int> ExportJsonlAsync(
        CloudApiClient api, Settings settings, CancellationToken ct)
    {
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
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));
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
        CommandContext context, Settings settings, CancellationToken ct)
    {
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
            var input = await AnsiConsole.AskAsync<string>("[green]>[/]");
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
        CommandContext context, Settings settings, CancellationToken ct) =>
        await new TuiCommand().ExecuteAsync(context,
            new TuiCommand.Settings
            {
                ServerUrl = settings.Url,
                ConversationId = settings.ConversationId,
            }, ct);
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
        CommandContext context, Settings settings, CancellationToken ct)
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
                    ServerTransport(s),
                    ServerTarget(s));
            }
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static string ServerTransport(JsonElement s)
    {
        if (s.TryGetProperty("transport", out var t))
        {
            return t.GetString() ?? "";
        }

        return s.TryGetProperty("type", out var t2)
            ? t2.GetString() ?? "" : "";
    }

    private static string ServerTarget(JsonElement s)
    {
        if (s.TryGetProperty("url", out var u))
        {
            return u.GetString() ?? "";
        }

        return s.TryGetProperty("command", out var c)
            ? c.GetString() ?? "" : "";
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
        CommandContext context, Settings settings, CancellationToken ct)
    {
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
            ? e.GetString() : ProbeDetail(probe);
        AnsiConsole.MarkupLine(ok
            ? $"[green]✓[/] {settings.Name}: {detail}"
            : $"[red]✗[/] {settings.Name}: {detail}");
        return ok ? 0 : 1;
    }

    private static string ProbeDetail(JsonElement probe) =>
        probe.TryGetProperty("tools", out var t)
            ? $"{t.GetArrayLength()} tools" : "connected";
}