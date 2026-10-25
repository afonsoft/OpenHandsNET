using System.Text.Json;

using OpenHandsNET.Cli.Services;
using OpenHandsNET.Eval;

using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenHandsNET.Cli.Commands;

/// <summary>
/// SPEC-39 RF-002: <c>openhandsnet eval run &lt;suite|task&gt;</c> —
/// runner headless: cria conversa, espera READY, roda asserts e grava
/// <c>result.json</c> por task. <c>--mode recorded</c> replaya
/// evidências gravadas (determinístico, sem LLM).
/// </summary>
public sealed class EvalRunCommand : AsyncCommand<EvalRunCommand.Settings>
{
    private const string RecordedMode = "recorded mode";
    /// <summary>CLI settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>Nome da suite em <c>evals/suites</c> ou task em <c>evals/tasks</c>.</summary>
        [CommandArgument(0, "<suite|task>")] public required string Target { get; set; }

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
        CommandContext ctx, Settings s, CancellationToken cancellationToken)
    {
        if (s.Mode is not "live" and not "recorded")
        {
            AnsiConsole.MarkupLine("[red]--mode deve ser live|recorded[/]");
            return 1;
        }

        var tasks = ResolveTasks(s, out var suiteName);
        if (tasks is null || tasks.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]nenhuma task resolvida[/]");
            return 1;
        }

        var recordings = new RecordingStore(
            Path.Combine(s.EvalsDir, "recordings"));

        var results = s.Mode == "recorded"
            ? await RunRecordedAsync(s, tasks, recordings, cancellationToken)
            : await RunLiveAsync(s, tasks, recordings, cancellationToken);
        if (results is null)
        {
            return 1;
        }

        var outDir = Path.Combine(s.Out,
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
                Json), cancellationToken);

        var regressions = CheckGate(s, results);
        if (s.Json)
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

        if (regressions.Count > 0 && s.Gate)
        {
            return 2;
        }

        return results.All(r => r.Pass) ? 0 : 1;
    }

    private static async Task<IReadOnlyList<EvalResult>> RunRecordedAsync(
        Settings s, IReadOnlyList<EvalTask> tasks,
        RecordingStore recordings, CancellationToken ct)
    {
        var runner = new EvalRunner(new NullBackend(), recordings);
        return await runner.RunAsync(tasks,
            new EvalRunOptions(s.Parallel, s.Model, "recorded"), ct: ct);
    }

    /// <summary>Modo live — null se o host remoto não tiver --allow-remote.</summary>
    private static async Task<IReadOnlyList<EvalResult>?> RunLiveAsync(
        Settings s, IReadOnlyList<EvalTask> tasks,
        RecordingStore recordings, CancellationToken ct)
    {
        var url = s.Url
            ?? Environment.GetEnvironmentVariable("OPENHANDSNET_SERVER_URL")
            ?? "http://localhost:8080";
        if (!url.StartsWith("http://localhost", StringComparison.Ordinal)
            && !url.StartsWith("http://127.0.0.1", StringComparison.Ordinal)
            && !s.AllowRemote)
        {
            AnsiConsole.MarkupLine(
                "[red]eval contra host remoto exige --allow-remote[/]");
            return null;
        }

        var http = new HttpClient();
        var api = await CloudApiClient.CreateAsync(
            s.Url, new CredentialStore(), http, ct);
        var runner = new EvalRunner(new EvalBackend(api, http),
            recordings);
        return await runner.RunAsync(tasks,
            new EvalRunOptions(s.Parallel, s.Model, "live", s.Record),
            onTaskDone: s.Json ? null : r => AnsiConsole.MarkupLine(
                r.Pass
                    ? $"[green]✓[/] {r.Name} [grey]({r.Score:F2})[/]"
                    : $"[red]✗[/] {r.Name} [grey]({r.Score:F2})[/]"),
            ct: ct);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private static List<EvalTask>? ResolveTasks(Settings s, out string suiteName)
    {
        var tasksDir = Path.Combine(s.EvalsDir, "tasks");
        var suitePath = Path.Combine(s.EvalsDir, "suites",
            s.Target.EndsWith(".yaml") ? s.Target : $"{s.Target}.yaml");
        var taskPath = Path.Combine(tasksDir,
            s.Target.EndsWith(".yaml") ? s.Target : $"{s.Target}.yaml");

        List<EvalTask> tasks;
        if (File.Exists(suitePath))
        {
            var suite = EvalParser.ParseSuite(File.ReadAllText(suitePath));
            suiteName = string.IsNullOrWhiteSpace(suite.Name)
                ? s.Target : suite.Name;
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
            suiteName = s.Target;
            tasks = [EvalParser.ParseTask(File.ReadAllText(taskPath))];
        }
        else if (File.Exists(s.Target))
        {
            suiteName = Path.GetFileNameWithoutExtension(s.Target);
            tasks = [EvalParser.ParseTask(File.ReadAllText(s.Target))];
        }
        else
        {
            suiteName = s.Target;
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
        Settings s, IReadOnlyList<EvalResult> results)
    {
        if (s.Baseline is null || !Directory.Exists(s.Baseline))
        {
            return [];
        }

        var baseline = Directory.EnumerateFiles(s.Baseline, "result.json",
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

/// <summary><c>eval report</c> — tabela + diff vs baseline.</summary>
public sealed class EvalReportCommand : Command<EvalReportCommand.Settings>
{
    /// <summary>CLI settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>Dir de resultados.</summary>
        [CommandArgument(0, "<results>")] public required string Dir { get; set; }

        /// <summary>Baseline dir p/ comparação.</summary>
        [CommandOption("--baseline")] public string? Baseline { get; set; }

        /// <summary>Exit non-zero em regressão (&gt;5% drop / pass→fail).</summary>
        [CommandOption("--gate")] public bool Gate { get; set; }

        /// <summary>JSON output.</summary>
        [CommandOption("--json")] public bool Json { get; set; }
    }

    /// <inheritdoc />
    public override int Execute(CommandContext ctx, Settings s, CancellationToken cancellationToken)
    {
        var results = LoadResults(s.Dir);
        var regressions = s.Baseline is { } b && Directory.Exists(b)
            ? Scorer.Regressions(results, LoadResults(b)) : [];

        if (s.Json)
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

        return regressions.Count > 0 && s.Gate ? 2 : 0;
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
    public override int Execute(CommandContext ctx, Settings s, CancellationToken cancellationToken)
    {
        var tasksDir = Path.Combine(s.EvalsDir, "tasks");
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
        var suitesDir = Path.Combine(s.EvalsDir, "suites");
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
                    ? "[green]ok[/]" : $"[red]{errs.Count} erro(s)[/]");
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