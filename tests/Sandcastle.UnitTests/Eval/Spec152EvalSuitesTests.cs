using System.Text.Json;

using Sandcastle.Application.Eval;
using Sandcastle.Infrastructure.Eval;

using Shouldly;

namespace Sandcastle.UnitTests.Eval;

/// <summary>
/// SPEC-152 RF-003 — suite adapters: swe-lite (jsonl embutido),
/// custom (jsonl do usuário) e terminal-bench-2 (fixture TB2
/// hand-authored — instruction.md + task.toml + tests/). Nenhum dado
/// real de benchmark é usado nos testes.
/// </summary>
public sealed class Spec152EvalSuitesTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"sc-eval-suites-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // ---------- swe-lite (embedded) ----------

    [Fact]
    public async Task SweLite_Loads_Embedded_Tasks_All_Valid()
    {
        var tasks = await new SweLiteSuiteAdapter().LoadAsync(
            new EvalSuiteLoadContext(null, null),
            CancellationToken.None);

        tasks.Count.ShouldBeInRange(15, 25); // subset curado ~20
        tasks.Select(t => t.TaskId).Distinct().Count()
            .ShouldBe(tasks.Count);
        tasks.ShouldAllBe(t =>
            EvalTaskParser.Validate(t) == null);
        tasks.ShouldAllBe(t => !string.IsNullOrEmpty(t.Image));
        tasks.ShouldAllBe(t => t.TimeoutS > 0);
        // Metadata carrega difficulty/category (controle de scorecard).
        tasks.ShouldAllBe(t =>
            t.Metadata!.ContainsKey("difficulty"));
    }

    // ---------- custom (user jsonl) ----------

    [Fact]
    public async Task Custom_Loads_Jsonl_From_TasksPath()
    {
        var path = Path.Combine(_dir, "tasks.jsonl");
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(path,
            "{\"task_id\":\"c1\",\"image\":\"i:1\",\"prompt\":\"p\","
            + "\"checker\":{\"cmd\":\"c\"}}\n"
            + "{\"task_id\":\"c2\",\"image\":\"i:2\",\"prompt\":\"p2\","
            + "\"checker\":{\"cmd\":\"c2\"}}\n");

        var tasks = await new CustomJsonlSuiteAdapter().LoadAsync(
            new EvalSuiteLoadContext(path, null), CancellationToken.None);

        tasks.Count.ShouldBe(2);
        tasks[0].TaskId.ShouldBe("c1");
    }

    [Fact]
    public async Task Custom_Without_Path_Or_Missing_File_Throws()
    {
        var adapter = new CustomJsonlSuiteAdapter();
        (await Should.ThrowAsync<EvalSuiteException>(
            () => adapter.LoadAsync(
                new EvalSuiteLoadContext(null, null),
                CancellationToken.None))).Message
            .ShouldContain("tasks_path");
        (await Should.ThrowAsync<EvalSuiteException>(
            () => adapter.LoadAsync(
                new EvalSuiteLoadContext(
                    Path.Combine(_dir, "nope.jsonl"), null),
                CancellationToken.None))).Message
            .ShouldContain("not found");
    }

    // ---------- terminal-bench-2 (hand-authored fixture) ----------

    private string WriteTb2Task(string taskDir, string toml,
        bool instruction = true,
        IReadOnlyDictionary<string, string>? tests = null)
    {
        var dir = Path.Combine(_dir, "tb2", taskDir);
        Directory.CreateDirectory(dir);
        if (instruction)
        {
            File.WriteAllText(Path.Combine(dir, "instruction.md"),
                "Fix the flaky service so `healthz` returns 200.\n");
        }

        File.WriteAllText(Path.Combine(dir, "task.toml"), toml);
        if (tests is not null)
        {
            var testsDir = Path.Combine(dir, "tests");
            Directory.CreateDirectory(testsDir);
            foreach (var (rel, content) in tests)
            {
                var path = Path.Combine(testsDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }
        }

        return dir;
    }

    private const string Tb2Toml = """
        version = "1.0"

        [metadata]
        author_name = "sandcastle-tests"
        difficulty = "medium"
        category = "sre"
        tags = ["networking", "debugging"]

        [agent]
        timeout_sec = 900

        [verifier]
        timeout_sec = 300

        [environment]
        docker_image = "ghcr.io/acme/tb2-task:latest"
        cpus = 1
        memory_mb = 2048
        """;

    [Fact]
    public async Task Tb2_Parses_Real_Format_Task_Dir()
    {
        WriteTb2Task("fix-healthz", Tb2Toml, tests:
            new Dictionary<string, string>
            {
                ["run-tests.sh"] = "#!/bin/bash\ncurl -sf localhost:8080/healthz\n",
            });
        var cache = Path.Combine(_dir, "cache");
        Directory.CreateDirectory(cache);
        Directory.Move(Path.Combine(_dir, "tb2"),
            Path.Combine(cache, "terminal-bench-2"));

        var tasks = await new TerminalBench2SuiteAdapter().LoadAsync(
            new EvalSuiteLoadContext(null, cache),
            CancellationToken.None);

        var task = tasks.ShouldHaveSingleItem();
        task.TaskId.ShouldBe("fix-healthz");
        task.Image.ShouldBe("ghcr.io/acme/tb2-task:latest");
        task.Prompt.ShouldContain("healthz");
        task.TimeoutS.ShouldBe(900); // agent.timeout_sec
        // Verifier files embutidos → path checker no /eval-verifier.
        task.Checker.Path.ShouldBe("/eval-verifier/run-tests.sh");
        task.Metadata!["difficulty"].GetString().ShouldBe("medium");
        task.Metadata["category"].GetString().ShouldBe("sre");
        task.Metadata["verifier_timeout_s"].GetInt32().ShouldBe(300);
        var files = task.Metadata["files"]
            .Deserialize<Dictionary<string, string>>()!;
        files.Keys.ShouldBe(["run-tests.sh"]);
        files["run-tests.sh"].ShouldContain("healthz");
    }

    [Fact]
    public async Task Tb2_VerifierCmd_Overrides_Checker()
    {
        var toml = Tb2Toml.Replace(
            "timeout_sec = 300\n",
            "timeout_sec = 300\n        cmd = \"pytest -x\"\n");
        WriteTb2Task("cmd-task", toml,
            tests: new Dictionary<string, string>
            { ["test_a.py"] = "assert True\n" });
        var cache = Path.Combine(_dir, "cache2");
        Directory.CreateDirectory(cache);
        Directory.Move(Path.Combine(_dir, "tb2"),
            Path.Combine(cache, "terminal-bench-2"));

        var tasks = await new TerminalBench2SuiteAdapter().LoadAsync(
            new EvalSuiteLoadContext(null, cache),
            CancellationToken.None);

        var task = tasks.ShouldHaveSingleItem();
        task.Checker.Cmd.ShouldBe("cd /eval-verifier && pytest -x");
    }

    [Fact]
    public async Task Tb2_No_DockerImage_Skipped_With_Error()
    {
        var toml = Tb2Toml.Replace(
            "docker_image = \"ghcr.io/acme/tb2-task:latest\"\n", "");
        WriteTb2Task("dockerfile-task", toml,
            tests: new Dictionary<string, string>
            { ["run-tests.sh"] = "#!/bin/bash\nexit 0\n" });
        File.WriteAllText(Path.Combine(_dir, "tb2",
            "dockerfile-task", "Dockerfile"), "FROM scratch\n");
        // Uma task válida junto → adapter carrega só ela.
        WriteTb2Task("good-task", Tb2Toml,
            tests: new Dictionary<string, string>
            { ["run-tests.sh"] = "x" });
        var cache = Path.Combine(_dir, "cache3");
        Directory.CreateDirectory(cache);
        Directory.Move(Path.Combine(_dir, "tb2"),
            Path.Combine(cache, "terminal-bench-2"));

        var tasks = await new TerminalBench2SuiteAdapter().LoadAsync(
            new EvalSuiteLoadContext(null, cache),
            CancellationToken.None);

        var task = tasks.ShouldHaveSingleItem();
        task.TaskId.ShouldBe("good-task");
    }

    [Fact]
    public async Task Tb2_Missing_Dataset_Dir_Throws()
    {
        var ex = await Should.ThrowAsync<EvalSuiteException>(
            () => new TerminalBench2SuiteAdapter().LoadAsync(
                new EvalSuiteLoadContext(null,
                    Path.Combine(_dir, "empty-cache")),
                CancellationToken.None));
        ex.Message.ShouldContain("dataset not found");
    }

    [Fact]
    public async Task Tb2_Tasks_Subdir_Convention_Probed()
    {
        // Dataset checkout raiz sem task dirs, mas com tasks/ — o
        // adapter deve descer p/ tasks/ automaticamente.
        WriteTb2Task("sub-task", Tb2Toml,
            tests: new Dictionary<string, string>
            { ["run-tests.sh"] = "x" });
        var cache = Path.Combine(_dir, "cache4");
        var dsRoot = Path.Combine(cache, "terminal-bench-2");
        Directory.CreateDirectory(dsRoot);
        Directory.Move(Path.Combine(_dir, "tb2"),
            Path.Combine(dsRoot, "tasks"));

        var tasks = await new TerminalBench2SuiteAdapter().LoadAsync(
            new EvalSuiteLoadContext(null, cache),
            CancellationToken.None);

        tasks.ShouldHaveSingleItem().TaskId.ShouldBe("sub-task");
    }

    // ---------- TomlReader ----------

    [Fact]
    public void TomlReader_Sections_Scalars_Arrays_Comments()
    {
        var doc = TomlReader.Parse("""
            # comment
            version = "1.0"
            n = 42
            f = 1.5
            b = true

            [environment]  # inline comment
            docker_image = "img:1"
            tags = ["a", "b c", 'd']
            """);

        doc.GetString("", "version").ShouldBe("1.0");
        doc.GetInt("", "n").ShouldBe(42);
        doc.GetDouble("", "f").ShouldBe(1.5);
        doc.GetBool("", "b").ShouldBe(true);
        doc.GetString("environment", "docker_image")
            .ShouldBe("img:1");
        doc.GetStringArray("environment", "tags")
            .ShouldBe(["a", "b c", "d"]);
        doc.GetString("missing", "key").ShouldBeNull();
    }

    [Fact]
    public void SuiteResolver_Maps_All_Suites()
    {
        var resolver = new EvalSuiteResolver(
        [
            new SweLiteSuiteAdapter(),
            new CustomJsonlSuiteAdapter(),
            new TerminalBench2SuiteAdapter(),
        ]);

        resolver.Get("swe-lite").ShouldBeOfType<SweLiteSuiteAdapter>();
        resolver.Get("custom")
            .ShouldBeOfType<CustomJsonlSuiteAdapter>();
        resolver.Get("terminal-bench-2")
            .ShouldBeOfType<TerminalBench2SuiteAdapter>();
        resolver.Get("bogus").ShouldBeNull();
    }
}