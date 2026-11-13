using System.Diagnostics;
using System.Text.Json;

using Sandcastle.Cli.Services;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Sandcastle.Cli.Commands;

/// <summary>
/// SPEC-152 RF-002/RF-003 — server-side eval verbs against
/// <c>/api/v1/evals</c>: <c>eval status</c> (runs + run detail),
/// <c>eval datasets fetch</c> (terminal-bench-2 dataset checkout para
/// o cache <c>Eval:DatasetCacheDir</c> — datasets nunca são commitados).
/// Shared helpers: tabela de runs, report md/json por run e scorecard.
/// </summary>
public static class EvalBench
{
    /// <summary>Resolved API client (--url > env > store > default).</summary>
    public static Task<CloudApiClient> ApiAsync(
        string? url, CancellationToken ct) =>
        CloudApiClient.CreateAsync(url, new CredentialStore(),
            new HttpClient(), ct);

    /// <summary>Imprime a tabela de runs (shape de GET /evals).</summary>
    public static int PrintRuns(JsonElement doc)
    {
        if (!doc.TryGetProperty("items", out var items))
        {
            AnsiConsole.MarkupLine("[red]resposta inválida[/]");
            return 1;
        }

        var table = new Table().AddColumns(
            "id", "suite", "model", "route", "status",
            "resolved%", "tasks", "cost", "created");
        foreach (var r in items.EnumerateArray())
        {
            table.AddRow(
                Short(r.GetProperty("id").GetString() ?? "?"),
                r.GetProperty("suite").GetString() ?? "?",
                Str(r, "model"), Str(r, "route_override"),
                StatusMarkup(r.GetProperty("status").GetString()),
                Str(r, "score_pct"),
                Str(r, "n_tasks"),
                Str(r, "actual_cost_usd"),
                Str(r, "created_at"));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    /// <summary>Report por run — tabela/json com verdicts por task.</summary>
    public static async Task<int> RunReportAsync(
        CloudApiClient api, string runId, string? format,
        CancellationToken ct)
    {
        var run = await api.GetEvalAsync(runId, ct);
        if (run is null)
        {
            AnsiConsole.MarkupLine("[red]run não encontrada[/]");
            return 1;
        }

        var results = await api.GetEvalResultsAsync(runId, ct);
        var r = run.Value;
        var rows = results.TryGetProperty("items", out var it)
            ? it.EnumerateArray().ToList() : [];
        if (format is "md" or "markdown")
        {
            Console.WriteLine(
                $"# Eval run {r.GetProperty("id").GetString()}\n");
            Console.WriteLine(
                $"- suite: `{Str(r, "suite")}` model: `{Str(r, "model")}`"
                + $" route: `{Str(r, "route_override")}`");
            Console.WriteLine(
                $"- status: **{Str(r, "status")}** — "
                + $"resolved **{Str(r, "score_pct")}%** "
                + $"({Str(r, "resolved_n")}/{Str(r, "n_tasks")})"
                + $" cost: ${Str(r, "actual_cost_usd")}\n");
            Console.WriteLine(
                "| task | verdict | duration_s | tokens | cost |");
            Console.WriteLine("|---|---|---|---|---|");
            foreach (var t in rows)
            {
                Console.WriteLine(
                    $"| {Str(t, "task_id")} | {Str(t, "verdict")} "
                    + $"| {Str(t, "duration_s")} | {Str(t, "tokens")} "
                    + $"| {Str(t, "cost_usd")} |");
            }

            return 0;
        }

        var table = new Table().AddColumns(
            "task", "verdict", "duration_s", "tokens", "cost");
        foreach (var t in rows)
        {
            table.AddRow(Str(t, "task_id"),
                VerdictMarkup(Str(t, "verdict")),
                Str(t, "duration_s"), Str(t, "tokens"),
                Str(t, "cost_usd"));
        }

        AnsiConsole.MarkupLine(
            $"[bold]{Str(r, "suite")}[/] {Str(r, "id")} — "
            + StatusMarkup(Str(r, "status"))
            + $" resolved [bold]{Str(r, "score_pct")}%[/] "
            + $"({Str(r, "resolved_n")}/{Str(r, "n_tasks")})"
            + $" cost ${Str(r, "actual_cost_usd")}");
        AnsiConsole.Write(table);
        return 0;
    }

    /// <summary>Scorecard — GET /evals/scorecard em md/json/table.</summary>
    public static async Task<int> ScorecardAsync(
        CloudApiClient api, string? suite, int last, string? format,
        bool json, CancellationToken ct)
    {
        var doc = await api.GetEvalScorecardAsync(suite, last, ct);
        if (json || format is "json")
        {
            AnsiConsole.WriteLine(doc.GetRawText());
            return 0;
        }

        var rows = doc.TryGetProperty("items", out var it)
            ? it.EnumerateArray().ToList() : [];
        if (format is "md" or "markdown")
        {
            Console.WriteLine(
                $"# Eval scorecard — suite `{suite ?? "all"}`\n");
            Console.WriteLine(
                "| model | route | runs | tasks | resolved | resolved% | cost |");
            Console.WriteLine("|---|---|---|---|---|---|---|");
            foreach (var r in rows)
            {
                Console.WriteLine(
                    $"| {Str(r, "model")} | {Str(r, "route")} "
                    + $"| {Str(r, "runs")} | {Str(r, "tasks")} "
                    + $"| {Str(r, "resolved")} "
                    + $"| {Str(r, "resolved_pct")} "
                    + $"| {Str(r, "total_cost_usd")} |");
            }

            return 0;
        }

        var table = new Table().AddColumns(
            "model", "route", "runs", "tasks",
            "resolved", "resolved%", "cost");
        foreach (var r in rows)
        {
            table.AddRow(Str(r, "model"), Str(r, "route"),
                Str(r, "runs"), Str(r, "tasks"), Str(r, "resolved"),
                Str(r, "resolved_pct"), Str(r, "total_cost_usd"));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    /// <summary>JSON string prop ou "".</summary>
    public static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p)
            ? p.ValueKind == JsonValueKind.Null
                ? "" : p.ToString()
            : "";

    private static string Short(string id) =>
        id.Length > 8 ? id[..8] : id;

    private static string StatusMarkup(string? s) => s switch
    {
        "completed" => "[green]completed[/]",
        "failed" => "[red]failed[/]",
        "cancelled" => "[yellow]cancelled[/]",
        "running" => "[blue]running[/]",
        _ => s ?? "?",
    };

    private static string VerdictMarkup(string s) => s switch
    {
        "resolved" => $"[green]{s}[/]",
        "failed" => $"[red]{s}[/]",
        "timeout" => $"[yellow]{s}[/]",
        "error" => $"[red]{s}[/]",
        _ => s,
    };
}

/// <summary><c>eval status [run-id]</c> — runs recentes ou detalhe.</summary>
public sealed class EvalStatusCommand
    : AsyncCommand<EvalStatusCommand.Settings>
{
    /// <summary>CLI settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>Run id (detalhe + results) — opcional.</summary>
        [CommandArgument(0, "[run-id]")]
        public string? RunId { get; set; }

        /// <summary>Filtra por suite.</summary>
        [CommandOption("--suite")] public string? Suite { get; set; }

        /// <summary>Filtra por status.</summary>
        [CommandOption("--status")] public string? Status { get; set; }

        /// <summary>Base URL do Api.</summary>
        [CommandOption("--url")] public string? Url { get; set; }

        /// <summary>JSON output.</summary>
        [CommandOption("--json")] public bool Json { get; set; }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        var api = await EvalBench.ApiAsync(
            settings.Url, cancellationToken);
        if (settings.RunId is { Length: > 0 } id)
        {
            var run = await api.GetEvalAsync(id, cancellationToken);
            if (run is null)
            {
                AnsiConsole.MarkupLine("[red]run não encontrada[/]");
                return 1;
            }

            if (settings.Json)
            {
                var results = await api.GetEvalResultsAsync(
                    id, cancellationToken);
                AnsiConsole.WriteLine(JsonSerializer.Serialize(new
                {
                    run = run.Value,
                    results,
                }));
                return 0;
            }

            return await EvalBench.RunReportAsync(
                api, id, null, cancellationToken);
        }

        var doc = await api.ListEvalsAsync(
            settings.Suite, settings.Status, ct: cancellationToken);
        if (settings.Json)
        {
            AnsiConsole.WriteLine(doc.GetRawText());
            return 0;
        }

        return EvalBench.PrintRuns(doc);
    }
}

/// <summary>
/// <c>eval datasets fetch</c> — clona o dataset público do
/// terminal-bench-2 para <c>Eval:DatasetCacheDir</c> (default
/// <c>eval-cache/terminal-bench-2</c>). Datasets NUNCA são commitados
/// (licença) — o adapter só lê o cache.
/// </summary>
public sealed class EvalDatasetsFetchCommand
    : AsyncCommand<EvalDatasetsFetchCommand.Settings>
{
    /// <summary>Default TB2 dataset git URL.</summary>
    public const string DefaultTb2Url =
        "https://github.com/laude-institute/terminal-bench.git";

    /// <summary>CLI settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>Suite/dataset a baixar (terminal-bench-2).</summary>
        [CommandArgument(0, "[suite]")]
        public string Suite { get; set; } = "terminal-bench-2";

        /// <summary>URL git/http alternativa do dataset.</summary>
        [CommandOption("--url")] public string? Url { get; set; }

        /// <summary>Dir destino (default eval-cache/{suite}).</summary>
        [CommandOption("--dest")] public string? Dest { get; set; }

        /// <summary>Base URL do Api (não usado — fetch é local).</summary>
        [CommandOption("--api-url")] public string? ApiUrl { get; set; }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Suite is not "terminal-bench-2")
        {
            AnsiConsole.MarkupLine(
                "[red]datasets fetch suporta só terminal-bench-2[/]");
            return 1;
        }

        var url = settings.Url ?? DefaultTb2Url;
        var dest = settings.Dest
            ?? Path.Combine("eval-cache", settings.Suite);
        if (Directory.Exists(dest)
            && Directory.EnumerateFileSystemEntries(dest).Any())
        {
            AnsiConsole.MarkupLine(
                $"[yellow]destino não-vazio[/] {dest} — usando git pull");
            var pull = await GitAsync(
                ["-C", dest, "pull", "--ff-only"], cancellationToken);
            return pull;
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(dest))!);
        AnsiConsole.MarkupLine(
            $"[grey]git clone --depth 1 {url} → {dest}[/]");
        return await GitAsync(
            ["clone", "--depth", "1", url, dest], cancellationToken);
    }

    private static async Task<int> GitAsync(
        IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("git not found");
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            var stderr = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
            {
                AnsiConsole.MarkupLine(
                    $"[red]git falhou[/] {stderr.Trim()}");
                return 1;
            }

            AnsiConsole.MarkupLine("[green]dataset fetch ok[/]");
            if (stderr.Trim().Length > 0)
            {
                AnsiConsole.MarkupLine($"[grey]{stderr.Trim()}[/]");
            }

            if (stdout.Trim().Length > 0)
            {
                AnsiConsole.WriteLine(stdout.Trim());
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(
                $"[red]git indisponível[/] {ex.Message} — faça o "
                + "checkout manual em eval-cache/terminal-bench-2");
            return 1;
        }
    }
}