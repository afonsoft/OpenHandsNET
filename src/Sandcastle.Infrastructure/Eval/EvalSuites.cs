using System.Text.Json;

using Sandcastle.Application.Eval;

namespace Sandcastle.Infrastructure.Eval;

/// <summary>
/// Suite load failure — a run whose adapter throws is marked
/// <c>failed</c> with this detail (never silently zero-tasks).
/// </summary>
public sealed class EvalSuiteException(string detail)
    : InvalidOperationException(detail);

/// <summary>
/// SPEC-152 RF-003 — <c>swe-lite</c>: curated smoke subset embedded as
/// <c>bench/swe-lite.jsonl</c> in this assembly (own content — no
/// third-party dataset). ~20 self-contained image tasks.
/// </summary>
public sealed class SweLiteSuiteAdapter : IEvalSuiteAdapter
{
    /// <summary>Embedded resource name (see csproj).</summary>
    public const string ResourceName = "Sandcastle.bench.swe-lite.jsonl";

    /// <inheritdoc />
    public string Suite => EvalSuites.SweLite;

    /// <inheritdoc />
    public Task<IReadOnlyList<EvalTaskV1>> LoadAsync(
        EvalSuiteLoadContext ctx, CancellationToken ct)
    {
        var asm = typeof(SweLiteSuiteAdapter).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new EvalSuiteException(
                $"embedded suite resource '{ResourceName}' missing");
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        var errors = new List<string>();
        var tasks = EvalTaskParser.ParseJsonl(content, errors);
        if (errors.Count > 0)
        {
            throw new EvalSuiteException(
                "swe-lite fixture has invalid tasks: "
                + string.Join("; ", errors));
        }

        if (tasks.Count == 0)
        {
            throw new EvalSuiteException("swe-lite fixture is empty");
        }

        return Task.FromResult<IReadOnlyList<EvalTaskV1>>(tasks);
    }
}

/// <summary>
/// SPEC-152 RF-003 — <c>custom</c>: user-supplied
/// <c>eval_task_v1</c> jsonl at <see cref="EvalSuiteLoadContext.TasksPath"/>.
/// </summary>
public sealed class CustomJsonlSuiteAdapter : IEvalSuiteAdapter
{
    /// <inheritdoc />
    public string Suite => EvalSuites.Custom;

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvalTaskV1>> LoadAsync(
        EvalSuiteLoadContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ctx.TasksPath))
        {
            throw new EvalSuiteException(
                "suite=custom requires tasks_path (jsonl file)");
        }

        if (!File.Exists(ctx.TasksPath))
        {
            throw new EvalSuiteException(
                $"tasks_path '{ctx.TasksPath}' not found");
        }

        var content = await File.ReadAllTextAsync(ctx.TasksPath, ct);
        var errors = new List<string>();
        var tasks = EvalTaskParser.ParseJsonl(content, errors);
        if (tasks.Count == 0)
        {
            throw new EvalSuiteException(
                "no valid eval_task_v1 tasks"
                + (errors.Count > 0
                    ? " — " + string.Join("; ", errors.Take(3)) : ""));
        }

        return tasks;
    }
}

/// <summary>
/// SPEC-152 RF-003 — <c>terminal-bench-2</c>: reads a user-provided
/// TB 2.0 dataset directory under <c>Eval:DatasetCacheDir</c>
/// (<c>{cache}/{dataset}/</c>; default dataset name = suite name).
/// Datasets are fetched out-of-band (<c>eval datasets fetch</c> /
/// manual checkout) — never committed (license guardrail).
///
/// TB2 task dir shape: <c>instruction.md</c> (prompt),
/// <c>task.toml</c> (<c>[metadata]</c>/<c>[agent]</c>/<c>[verifier]</c>/
/// <c>[environment] docker_image</c>), optional <c>Dockerfile</c>,
/// <c>tests/</c> verifier scripts. Mapping → <c>eval_task_v1</c>:
/// image = docker_image (Dockerfile-only tasks are skipped — image
/// builds are out of scope), verifier files are embedded in
/// <c>metadata.files</c> and materialized under <c>/eval-verifier/</c>
/// in the sandbox; <c>verifier.cmd</c> overrides the checker command.
/// </summary>
public sealed class TerminalBench2SuiteAdapter : IEvalSuiteAdapter
{
    /// <summary>Sandbox dir where verifier files are materialized.</summary>
    public const string VerifierDir = "/eval-verifier";

    /// <summary>Default checker when no verifier.cmd is declared.</summary>
    public const string DefaultVerifierCmd =
        "cd /eval-verifier && bash run-tests.sh";

    /// <summary>Per-file embed cap (bytes).</summary>
    public const int MaxFileBytes = 64 * 1024;

    /// <summary>Total embed cap per task (bytes).</summary>
    public const int MaxTotalFileBytes = 512 * 1024;

    /// <inheritdoc />
    public string Suite => EvalSuites.TerminalBench2;

    /// <inheritdoc />
    public Task<IReadOnlyList<EvalTaskV1>> LoadAsync(
        EvalSuiteLoadContext ctx, CancellationToken ct)
    {
        var root = ctx.DatasetCacheDir is { Length: > 0 } cache
            ? Path.Combine(cache,
                ctx.DatasetName ?? EvalSuites.TerminalBench2)
            : null;
        if (root is null || !Directory.Exists(root))
        {
            throw new EvalSuiteException(
                "terminal-bench-2 dataset not found — fetch it into "
                + $"'{ctx.DatasetCacheDir}/{ctx.DatasetName
                    ?? EvalSuites.TerminalBench2}' (eval datasets fetch)");
        }

        // Checkouts TB2 mantêm as tasks num subdir tasks/ — o adapter
        // aceita a raiz do dataset ou o dir de tasks diretamente.
        if (!Directory.EnumerateDirectories(root).Any(d =>
                File.Exists(Path.Combine(d, "task.toml")))
            && Directory.Exists(Path.Combine(root, "tasks")))
        {
            root = Path.Combine(root, "tasks");
        }

        var tasks = new List<EvalTaskV1>();
        var errors = new List<string>();
        foreach (var dir in Directory
            .EnumerateDirectories(root).OrderBy(d => d,
                StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var task = TryLoadTask(dir, errors);
            if (task is not null)
            {
                tasks.Add(task);
            }
        }

        if (tasks.Count == 0)
        {
            throw new EvalSuiteException(
                "no loadable terminal-bench-2 tasks under " + root
                + (errors.Count > 0
                    ? " — " + string.Join("; ", errors.Take(3)) : ""));
        }

        return Task.FromResult<IReadOnlyList<EvalTaskV1>>(tasks);
    }

    private static EvalTaskV1? TryLoadTask(
        string dir, List<string> errors)
    {
        var taskId = Path.GetFileName(dir.TrimEnd(
            Path.DirectorySeparatorChar));
        var instructionPath = Path.Combine(dir, "instruction.md");
        var tomlPath = Path.Combine(dir, "task.toml");
        if (!File.Exists(instructionPath) || !File.Exists(tomlPath))
        {
            return null; // diretório não-task (docs, .git…) — ignora.
        }

        TomlReader toml;
        try
        {
            toml = TomlReader.Parse(File.ReadAllText(tomlPath));
        }
        catch (FormatException ex)
        {
            errors.Add($"{taskId}: task.toml — {ex.Message}");
            return null;
        }

        var image = toml.GetString("environment", "docker_image");
        if (string.IsNullOrWhiteSpace(image))
        {
            // Dockerfile-only TB2 tasks precisam de build de imagem —
            // fora de escopo do adapter (o spec exige image pronta).
            errors.Add($"{taskId}: environment.docker_image missing"
                + " (Dockerfile builds unsupported)");
            return null;
        }

        var prompt = File.ReadAllText(instructionPath).Trim();
        if (prompt.Length == 0)
        {
            errors.Add($"{taskId}: empty instruction.md");
            return null;
        }

        var files = EmbedVerifierFiles(dir, taskId, errors);
        var verifierCmd = toml.GetString("verifier", "cmd");
        EvalTaskChecker checker;
        if (!string.IsNullOrWhiteSpace(verifierCmd))
        {
            checker = new EvalTaskChecker
            { Cmd = $"cd {VerifierDir} && {verifierCmd}" };
        }
        else if (files.Count > 0 && files.ContainsKey("run-tests.sh"))
        {
            checker = new EvalTaskChecker
            { Path = $"{VerifierDir}/run-tests.sh" };
        }
        else if (files.Count > 0)
        {
            checker = new EvalTaskChecker
            { Cmd = DefaultVerifierCmd };
        }
        else
        {
            errors.Add($"{taskId}: no verifier (tests/ files or"
                + " verifier.cmd required)");
            return null;
        }

        var metadata = new Dictionary<string, JsonElement>
        {
            ["source"] = JsonSerializer.SerializeToElement(
                EvalSuites.TerminalBench2),
            ["tb_task"] = JsonSerializer.SerializeToElement(taskId),
        };
        if (toml.GetString("metadata", "difficulty") is { } diff)
        {
            metadata["difficulty"] = JsonSerializer
                .SerializeToElement(diff);
        }

        if (toml.GetString("metadata", "category") is { } cat)
        {
            metadata["category"] = JsonSerializer.SerializeToElement(cat);
        }

        if (toml.GetInt("verifier", "timeout_sec") is { } vt)
        {
            metadata["verifier_timeout_s"] =
                JsonSerializer.SerializeToElement(vt);
        }

        if (files.Count > 0)
        {
            metadata["files"] = JsonSerializer.SerializeToElement(files);
        }

        return new EvalTaskV1
        {
            TaskId = taskId,
            Image = image,
            Prompt = prompt,
            Checker = checker,
            TimeoutS = toml.GetInt("agent", "timeout_sec"),
            Metadata = metadata,
        };
    }

    /// <summary>Embeds <c>tests/</c> files (text, capped) keyed by relpath.</summary>
    private static Dictionary<string, string> EmbedVerifierFiles(
        string taskDir, string taskId, List<string> errors)
    {
        var files = new Dictionary<string, string>(
            StringComparer.Ordinal);
        var testsDir = Path.Combine(taskDir, "tests");
        if (!Directory.Exists(testsDir))
        {
            // Convenção TB2 alternativa: scripts na raiz da task.
            testsDir = taskDir;
        }

        var total = 0L;
        foreach (var path in Directory
            .EnumerateFiles(testsDir, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            // task.toml / instruction.md / Dockerfile não são verifiers.
            if (testsDir == taskDir
                && name is "task.toml" or "instruction.md"
                    or "Dockerfile")
            {
                continue;
            }

            var rel = testsDir == taskDir
                ? name
                : Path.GetRelativePath(testsDir, path)
                    .Replace('\\', '/');
            var size = new FileInfo(path).Length;
            if (size > MaxFileBytes)
            {
                errors.Add($"{taskId}: verifier file '{rel}' exceeds"
                    + $" {MaxFileBytes}B — skipped");
                continue;
            }

            total += size;
            if (total > MaxTotalFileBytes)
            {
                errors.Add($"{taskId}: verifier files exceed"
                    + $" {MaxTotalFileBytes}B — truncated");
                break;
            }

            files[rel] = File.ReadAllText(path);
        }

        return files;
    }
}

/// <summary>
/// SPEC-152 RF-003 — suite registry: resolves suite key → adapter
/// (DI collects all <see cref="IEvalSuiteAdapter"/> implementations).
/// </summary>
public sealed class EvalSuiteResolver(
    IEnumerable<IEvalSuiteAdapter> adapters) : IEvalSuiteResolver
{
    private readonly Dictionary<string, IEvalSuiteAdapter> _map =
        adapters.ToDictionary(a => a.Suite, StringComparer.Ordinal);

    /// <inheritdoc />
    public IEvalSuiteAdapter? Get(string suite) =>
        _map.TryGetValue(suite, out var a) ? a : null;
}