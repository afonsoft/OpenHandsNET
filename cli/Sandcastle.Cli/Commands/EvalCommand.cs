using System.Net.Http.Json;
using System.Text.Json;

using Sandcastle.Cli.Services;
using Sandcastle.Eval;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Sandcastle.Cli.Commands;

/// <summary>
/// SPEC-39 RF-002: <c>sandcastle eval run &lt;suite|task&gt;</c> —
/// runner headless local (yaml): cria conversa, espera READY, roda
/// asserts e grava <c>result.json</c> por task. <c>--mode recorded</c>
/// replaya evidências gravadas (determinístico, sem LLM).
/// SPEC-152: <c>--suite swe-lite|terminal-bench-2|custom</c> roda o
/// benchmark no servidor (<c>POST /api/v1/evals</c>) — hermetic,
/// scorecard via <c>eval report --suite</c>.
/// </summary>
public sealed class EvalRunCommand : AsyncCommand<EvalRunCommand.Settings>
{
    private const string RecordedMode = "recorded mode";
    /// <summary>CLI settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>Nome da suite em <c>evals/suites</c> ou task em <c>evals/tasks</c> (modo local SPEC-39).</summary>
        [CommandArgument(0, "[suite|task]")] public string? Target { get; set; }

        /// <summary>SPEC-152: suite server-side (swe-lite|terminal-bench-2|custom).</summary>
        [CommandOption("--suite")] public string? Suite { get; set; }

        /// <summary>SPEC-152: tasks por run (default: todas).</summary>
        [CommandOption("--n")] public int? N { get; set; }

        /// <summary>SPEC-152: label de route p/ scorecard comparativo.</summary>
        [CommandOption("--route")] public string? Route { get; set; }

        /// <summary>SPEC-152: jsonl eval_task_v1 (suite=custom).</summary>
        [CommandOption("--tasks-path")] public string? TasksPath { get; set; }

        /// <summary>SPEC-152: confirma a estimativa quando n > gate (25).</summary>
        [CommandOption("--yes")] public bool EstimateAcknowledged { get; set; }

        /// <summary>SPEC-152: aguarda o término da run (poll status).</summary>
        [CommandOption("--watch")] public bool Watch { get; set; }

        /// <summary>Base URL do Api.</summary>
        [CommandOption("--url")] public string? Url { get; set; }

        /// <summary>Paralelismo de tasks.</summary>
        [CommandOption("--parallel")] public int Parallel { get; set; } = 1;

        /// <summary>Override do modelo LLM.</summary>
        [CommandOption("--model")] public string? Model { get; set; }

        /// <summary>Profile do agente (repassado no prompt/run).</summary>
        [CommandOption("--agent-profile")] public string? AgentProfile { get; set; }

        /// <summary>Diretório de saída p/ result.json.</summary>
        [CommandOption("--out")] public string Out { get; set; } = "evals/results";

        /// <summary>Dir raiz dos fixtures (tasks/, suites/, recordings/).</summary>
        [CommandOption("--evals-dir")] public string EvalsDir { get; set; } = "evals";

        /// <summary><c>live</c> | <c>recorded</c> (replay sem LLM).</summary>
        [CommandOption("--mode")] public string Mode { get; set; } = "live";

        /// <summary>Grava evidências p/ replay recorded.</summary>
        [CommandOption("--record")] public bool Record { get; set; }

        /// <summary>Baseline dir p/ diff + gate.</summary>
        [CommandOption("--baseline")] public string? Baseline { get; set; }

        /// <summary>Exit non-zero se houver regressão vs baseline.</summary>
        [CommandOption("--gate")] public bool Gate { get; set; }

        /// <summary>Necessário para provider remote.</summary>
        [CommandOption("--allow-remote")] public bool AllowRemote { get; set; }

        /// <summary>JSON output (CI).</summary>
        [CommandOption("--json")] public bool Json { get; set; }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // SPEC-152: modo server-side (--suite) → POST /api/v1/evals.
        if (settings.Suite is { Length: > 0 })
        {
            return await RunServerBenchmarkAsync(
                settings, cancellationToken);
        }

        if (settings.Target is null)
        {
            AnsiConsole.MarkupLine(
                "[red]informe <suite|task> (yaml local) ou "
                + "--suite <benchmark> (server-side)[/]");
            return 1;
        }

        if (settings.Mode is not "live" and not "recorded")
        {
            AnsiConsole.MarkupLine("[red]--mode deve ser live|recorded[/]");
            return 1;
        }

        var tasks = ResolveTasks(settings, out var suiteName);
        if (tasks is null || tasks.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]nenhuma task resolvida[/]");
            return 1;
        }

        var recordings = new RecordingStore(
            Path.Combine(settings.EvalsDir, "recordings"));

        var results = settings.Mode == "recorded"
            ? await RunRecordedAsync(settings, tasks, recordings, cancellationToken)
            : await RunLiveAsync(settings, tasks, recordings, cancellationToken);
        if (results is null)
        {
            return 1;
        }

        var outDir = Path.Combine(settings.Out,
            $"{suiteName}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
        Directory.CreateDirectory(outDir);
        foreach (var r in results)
        {
            var perTask = Path.Combine(outDir, r.Name);
            Directory.CreateDirectory(perTask);
            await File.WriteAllTextAsync(
                Path.Combine(perTask, "result.json"),
                EvalResult.ToJson(r), cancellationToken);
        }

        await File.WriteAllTextAsync(
            Path.Combine(outDir, "suite.json"), JsonSerializer.Serialize(
                new
                {
                    suite = suiteName,
                    total = results.Count,
                    pass = results.Count(r => r.Pass),
                    score = results.Average(r => r.Score)
                },
                JsonOpts), cancellationToken);

        var regressions = CheckGate(settings, results);
        if (settings.Json)
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(new
            {
                suite = suiteName,
                out_dir = outDir,
                total = results.Count,
                pass = results.Count(r => r.Pass),
                score = results.Average(r => r.Score),
                regressions = regressions.Select(r => r.Task).ToArray(),
            }));
        }
        else
        {
            var score = results.Average(r => r.Score);
            AnsiConsole.MarkupLine(
                $"[bold]{results.Count(r => r.Pass)}/{results.Count}[/] "
                + $"passaram — score {score:F2} → {outDir}");
            foreach (var reg in regressions)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]REGRESSÃO[/] {reg.Task} "
                    + $"({reg.BaselineScore:F2} → {reg.Score:F2})");
            }
        }

        if (regressions.Count > 0 && settings.Gate)
        {
            return 2;
        }

        return results.All(r => r.Pass) ? 0 : 1;
    }

    private static async Task<IReadOnlyList<EvalResult>> RunRecordedAsync(
        Settings settings, IReadOnlyList<EvalTask> tasks,
        RecordingStore recordings, CancellationToken ct)
    {
        var runner = new EvalRunner(new NullBackend(), recordings);
        return await runner.RunAsync(tasks,
            new EvalRunOptions(settings.Parallel, settings.Model, "recorded"), ct: ct);
    }

    /// <summary>Modo live — null se o host remoto não tiver --allow-remote.</summary>
    private static async Task<IReadOnlyList<EvalResult>?> RunLiveAsync(
        Settings settings, IReadOnlyList<EvalTask> tasks,
        RecordingStore recordings, CancellationToken ct)
    {
        var url = settings.Url
            ?? Environment.GetEnvironmentVariable("SANDCASTLE_SERVER_URL")
            ?? CliDefaults.DefaultServerUrl;
        if (!url.StartsWith("http://localhost", StringComparison.Ordinal)
            && !url.StartsWith("http://127.0.0.1", StringComparison.Ordinal)
            && !settings.AllowRemote)
        {
            AnsiConsole.MarkupLine(
                "[red]eval contra host remoto exige --allow-remote[/]");
            return null;
        }

        var http = new HttpClient();
        var api = await CloudApiClient.CreateAsync(
            settings.Url, new CredentialStore(), http, ct);
        var runner = new EvalRunner(new EvalBackend(api, http),
            recordings);
        return await runner.RunAsync(tasks,
            new EvalRunOptions(settings.Parallel, settings.Model, "live", settings.Record),
            onTaskDone: settings.Json ? null : r => AnsiConsole.MarkupLine(
                r.Pass
                    ? $"[green]✓[/] {r.Name} [grey]({r.Score:F2})[/]"
                    : $"[red]✗[/] {r.Name} [grey]({r.Score:F2})[/]"),
            ct: ct);
    }

    /// <summary>
    /// SPEC-152 RF-002: server-side benchmark — POST /api/v1/evals,
    /// trata o gate de estimativa e (opcional) aguarda o fim via
    /// <c>--watch</c>. Exit 0 = run aceita; não-zero = rejeitada.
    /// </summary>
    private static async Task<int> RunServerBenchmarkAsync(
        Settings settings, CancellationToken ct)
    {
        var api = await EvalBench.ApiAsync(settings.Url, ct);
        var resp = await api.CreateEvalAsync(
            settings.Suite!, settings.Model, settings.Route,
            settings.N, settings.TasksPath,
            settings.EstimateAcknowledged, ct);
        JsonElement body;
        try
        {
            body = await resp.Content
                .ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (JsonException)
        {
            body = JsonDocument.Parse("{}").RootElement.Clone();
        }

        if (body.ValueKind == JsonValueKind.Undefined)
        {
            body = JsonDocument.Parse("{}").RootElement.Clone();
        }

        if ((int)resp.StatusCode == 400
            && body.TryGetProperty("error", out var err)
            && err.GetString() == "estimate_required")
        {
            var est = body.GetProperty("estimate");
            AnsiConsole.MarkupLine(
                $"[yellow]estimate gate[/] n={EvalBench.Str(est, "n")}"
                + $" tasks > gate — custo estimado "
                + $"${EvalBench.Str(est, "estimated_cost_usd")} "
                + $"({EvalBench.Str(est, "warning")})");
            AnsiConsole.MarkupLine(
                "[grey]repita com --yes para confirmar a estimativa[/]");
            return 2;
        }

        if (!resp.IsSuccessStatusCode)
        {
            var detail = body.TryGetProperty("detail", out var d)
                ? d.GetString() : resp.ReasonPhrase;
            AnsiConsole.MarkupLine(
                $"[red]run rejeitada ({(int)resp.StatusCode})[/] {detail}");
            return 1;
        }

        var runId = body.GetProperty("id").GetString()!;
        if (settings.Json)
        {
            AnsiConsole.WriteLine(body.GetRawText());
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[green]run {runId[..8]}[/] {settings.Suite} "
                + $"— {EvalBench.Str(body, "n_tasks")} tasks "
                + $"[grey](eval status {runId})[/]");
        }

        if (!settings.Watch)
        {
            return 0;
        }

        return await WatchRunAsync(api, runId, settings, ct);
    }

    /// <summary>Polls GET /evals/{id} até estado terminal.</summary>
    private static async Task<int> WatchRunAsync(
        CloudApiClient api, string runId, Settings settings,
        CancellationToken ct)
    {
        string? last = null;
        while (!ct.IsCancellationRequested)
        {
            var run = await api.GetEvalAsync(runId, ct);
            if (run is null)
            {
                AnsiConsole.MarkupLine("[red]run sumiu[/]");
                return 1;
            }

            var status = EvalBench.Str(run.Value, "status");
            if (status != last && !settings.Json)
            {
                AnsiConsole.MarkupLine(
                    $"[grey]→[/] {status} "
                    + $"({EvalBench.Str(run.Value, "resolved_n")}/"
                    + $"{EvalBench.Str(run.Value, "n_tasks")} resolved)");
                last = status;
            }

            if (status is "completed" or "failed" or "cancelled")
            {
                if (settings.Json)
                {
                    AnsiConsole.WriteLine(run.Value.GetRawText());
                }
                else
                {
                    await EvalBench.RunReportAsync(
                        api, runId, null, ct);
                }

                return status == "completed" ? 0 : 1;
            }

            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }

        return 1;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private static List<EvalTask>? ResolveTasks(Settings settings, out string suiteName)
    {
        var tasksDir = Path.Combine(settings.EvalsDir, "tasks");
        var target = settings.Target!;
        var suitePath = Path.Combine(settings.EvalsDir, "suites",
            target.EndsWith(".yaml") ? target : $"{target}.yaml");
        var taskPath = Path.Combine(tasksDir,
            target.EndsWith(".yaml") ? target : $"{target}.yaml");

        List<EvalTask> tasks;
        if (File.Exists(suitePath))
        {
            var suite = EvalParser.ParseSuite(File.ReadAllText(suitePath));
            suiteName = string.IsNullOrWhiteSpace(suite.Name)
                ? target : suite.Name;
            tasks = suite.Tasks
                .Select(t => EvalParser.ParseTask(File.ReadAllText(
                    Path.Combine(tasksDir,
                        t.EndsWith(".yaml") ? t : $"{t}.yaml"))))
                .Where(t => suite.Tags.Count == 0
                    || t.Tags.Intersect(suite.Tags).Any())
                .ToList();
        }
        else if (File.Exists(taskPath))
        {
            suiteName = target;
            tasks = [EvalParser.ParseTask(File.ReadAllText(taskPath))];
        }
        else if (File.Exists(target))
        {
            suiteName = Path.GetFileNameWithoutExtension(target);
            tasks = [EvalParser.ParseTask(File.ReadAllText(target))];
        }
        else
        {
            suiteName = target;
            return null;
        }

        var bad = tasks
            .SelectMany(t => EvalParser.Validate(t)
                .Select(e => $"{t.Name}: {e}"))
            .ToList();
        foreach (var e in bad)
        {
            AnsiConsole.MarkupLine($"[red]inválido[/] {e}");
        }

        return bad.Count > 0 ? null : tasks;
    }

    private static IReadOnlyList<Regression> CheckGate(
        Settings settings, IReadOnlyList<EvalResult> results)
    {
        if (settings.Baseline is null || !Directory.Exists(settings.Baseline))
        {
            return [];
        }

        var baseline = Directory.EnumerateFiles(settings.Baseline, "result.json",
                SearchOption.AllDirectories)
            .Select(f => EvalResult.FromJson(File.ReadAllText(f)))
            .OfType<EvalResult>()
            .ToList();
        return baseline.Count == 0
            ? [] : Scorer.Regressions(results, baseline);
    }

    /// <summary>Backend nulo p/ modo recorded (não toca na API).</summary>
    private sealed class NullBackend : IEvalBackend
    {
        public Task<string> CreateConversationAsync(
            string task, string? repo, string? model, CancellationToken ct) =>
            throw new InvalidOperationException(RecordedMode);
        public Task<string> WaitReadyAsync(string taskId, CancellationToken ct) =>
            throw new InvalidOperationException(RecordedMode);
        public Task RunSetupAsync(
            string conversationId, IReadOnlyList<string> commands, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<JsonElement>> WatchAsync(
            string conversationId, TimeSpan timeout, CancellationToken ct) =>
            throw new InvalidOperationException(RecordedMode);
        public Task<(int, string)> ExecAsync(
            string conversationId, string command, CancellationToken ct) =>
            throw new InvalidOperationException(RecordedMode);
        public Task<string?> ReadFileAsync(
            string conversationId, string path, CancellationToken ct) =>
            throw new InvalidOperationException(RecordedMode);
        public Task<(double, string)?> JudgeAsync(
            string rubric, string transcript, CancellationToken ct) =>
            Task.FromResult<(double, string)?>(null);
    }
}

/// <summary>
/// <c>eval report</c> — SPEC-39: tabela + diff vs baseline de um dir
/// de resultados locais (posicional). SPEC-152: <c>--run &lt;id&gt;</c>
/// report por run server-side; <c>--suite &lt;name&gt;</c> scorecard
/// comparativo (resolved% por model/route) —
/// <c>--format table|md|json</c>.
/// </summary>
public sealed class EvalReportCommand : AsyncCommand<EvalReportCommand.Settings>
{
    /// <summary>CLI settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>Dir de resultados (modo local SPEC-39).</summary>
        [CommandArgument(0, "[results]")] public string? Dir { get; set; }

        /// <summary>SPEC-152: run id — report por task.</summary>
        [CommandOption("--run")] public string? RunId { get; set; }

        /// <summary>SPEC-152: suite — scorecard comparativo.</summary>
        [CommandOption("--suite")] public string? Suite { get; set; }

        /// <summary>SPEC-152: últimas N runs no scorecard.</summary>
        [CommandOption("--last")] public int Last { get; set; } = 10;

        /// <summary>SPEC-152: table|md|json (default table).</summary>
        [CommandOption("--format")] public string? Format { get; set; }

        /// <summary>Base URL do Api (modo server-side).</summary>
        [CommandOption("--url")] public string? Url { get; set; }

        /// <summary>Baseline dir p/ comparação.</summary>
        [CommandOption("--baseline")] public string? Baseline { get; set; }

        /// <summary>Exit non-zero em regressão (&gt;5% drop / pass→fail).</summary>
        [CommandOption("--gate")] public bool Gate { get; set; }

        /// <summary>JSON output.</summary>
        [CommandOption("--json")] public bool Json { get; set; }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        // SPEC-152: modos server-side — report por run ou scorecard.
        if (settings.RunId is { Length: > 0 } runId)
        {
            var api = await EvalBench.ApiAsync(
                settings.Url, cancellationToken);
            if (settings.Json || settings.Format is "json")
            {
                var runResults = await api.GetEvalResultsAsync(
                    runId, cancellationToken);
                AnsiConsole.WriteLine(runResults.GetRawText());
                return 0;
            }

            return await EvalBench.RunReportAsync(
                api, runId, settings.Format, cancellationToken);
        }

        if (settings.Suite is { Length: > 0 } suite)
        {
            var api = await EvalBench.ApiAsync(
                settings.Url, cancellationToken);
            return await EvalBench.ScorecardAsync(
                api, suite, settings.Last, settings.Format,
                settings.Json, cancellationToken);
        }

        if (settings.Dir is null)
        {
            AnsiConsole.MarkupLine(
                "[red]informe [results] (local), --run <id> ou "
                + "--suite <name> (scorecard)[/]");
            return 1;
        }

        var results = LoadResults(settings.Dir);
        var regressions = settings.Baseline is { } b && Directory.Exists(b)
            ? Scorer.Regressions(results, LoadResults(b)) : [];

        if (settings.Json)
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(new
            {
                total = results.Count,
                pass = results.Count(r => r.Pass),
                score = results.Count > 0 ? results.Average(r => r.Score) : 0,
                regressions = regressions.Select(r => new
                {
                    task = r.Task,
                    baseline = r.BaselineScore,
                    score = r.Score,
                }).ToArray(),
            }));
        }
        else
        {
            var table = new Table().AddColumns(
                "task", "score", "status", "tokens", "duração");
            foreach (var r in results)
            {
                table.AddRow(r.Name, $"{r.Score:F2}",
                    r.Pass ? "[green]PASS[/]" : "[red]FAIL[/]",
                    r.Tokens.ToString(), $"{r.DurationMs / 1000.0:F0}s");
            }

            AnsiConsole.Write(table);
            foreach (var reg in regressions)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]REGRESSÃO[/] {reg.Task} "
                    + $"({reg.BaselineScore:F2} → {reg.Score:F2})");
            }
        }

        return regressions.Count > 0 && settings.Gate ? 2 : 0;
    }

    private static List<EvalResult> LoadResults(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "result.json",
                    SearchOption.AllDirectories)
                .Select(f => EvalResult.FromJson(File.ReadAllText(f)))
                .OfType<EvalResult>().ToList()
            : [];
}

/// <summary><c>eval list</c> — lista + valida tasks/suites do diretório.</summary>
public sealed class EvalListCommand : Command<EvalListCommand.Settings>
{
    /// <summary>CLI settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>Dir raiz dos fixtures.</summary>
        [CommandOption("--evals-dir")] public string EvalsDir { get; set; } = "evals";
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var tasksDir = Path.Combine(settings.EvalsDir, "tasks");
        var errors = 0;
        var table = new Table().AddColumns("task", "asserts", "tags", "status");
        if (Directory.Exists(tasksDir))
        {
            foreach (var f in Directory.EnumerateFiles(tasksDir, "*.yaml"))
            {
                errors += ValidateTaskFile(f, table);
            }
        }

        AnsiConsole.Write(table);
        var suitesDir = Path.Combine(settings.EvalsDir, "suites");
        if (Directory.Exists(suitesDir))
        {
            foreach (var f in Directory.EnumerateFiles(suitesDir, "*.yaml"))
            {
                AnsiConsole.MarkupLine(
                    $"[grey]suite[/] {Path.GetFileNameWithoutExtension(f)}");
            }
        }

        return errors > 0 ? 1 : 0;
    }

    /// <summary>Valida um task yaml: linha na tabela + retorna nº de erros.</summary>
    private static int ValidateTaskFile(string f, Table table)
    {
        try
        {
            var t = EvalParser.ParseTask(File.ReadAllText(f));
            var errs = EvalParser.Validate(t);
            table.AddRow(t.Name,
                string.Join(",", t.Assert.Select(a => a.Type)),
                string.Join(",", t.Tags),
                errs.Count == 0
                    ? "[green]ok[/]" : $"[red]{errs.Count} erro(settings)[/]");
            foreach (var e in errs)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {f}: {e}");
            }

            return errs.Count;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] {f}: {ex.Message}");
            return 1;
        }
    }
}