using System.Text.Json;

using OpenHandsNET.Eval;

using Shouldly;

namespace OpenHandsNET.UnitTests.Eval;

/// <summary>
/// SPEC-39: parser/validator de tasks + scorer + gate de regressão +
/// recorded mode (replay determinístico sem backend).
/// </summary>
public sealed class EvalHarnessTests
{
    private const string ValidTask = """
        name: fix-test
        task: "Fix the failing test"
        timeout: 5m
        assert:
          - type: exit_code
            cmd: "pytest -x"
            expect: 0
          - type: file_contains
            path: /workspace/x.py
            pattern: "def add"
        tags: [bugfix]
        """;

    [Fact]
    public void Parser_RoundTrips_TaskSchema()
    {
        var t = EvalParser.ParseTask(ValidTask);
        t.Name.ShouldBe("fix-test");
        t.Task.ShouldBe("Fix the failing test");
        t.Assert.Count.ShouldBe(2);
        t.Assert[0].Type.ShouldBe("exit_code");
        t.Assert[0].Cmd.ShouldBe("pytest -x");
        t.Assert[0].Expect.ShouldBe(0);
        t.Assert[1].Path.ShouldBe("/workspace/x.py");
        t.Tags.ShouldBe(["bugfix"]);
        EvalParser.Validate(t).ShouldBeEmpty();
    }

    [Fact]
    public void Parser_Timeout_Parses_Suffixes()
    {
        EvalParser.ParseTimeout("90s").ShouldBe(TimeSpan.FromSeconds(90));
        EvalParser.ParseTimeout("10m").ShouldBe(TimeSpan.FromMinutes(10));
        EvalParser.ParseTimeout("2h").ShouldBe(TimeSpan.FromHours(2));
        EvalParser.ParseTimeout("").ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Validator_Flags_Missing_Fields()
    {
        var t = EvalParser.ParseTask("""
            name: bad
            assert:
              - type: exit_code
              - type: llm_judge
              - type: nope
            """);
        var errors = EvalParser.Validate(t);
        errors.ShouldContain(e => e.Contains("task is required"));
        errors.ShouldContain(e => e.Contains("exit_code requires cmd"));
        errors.ShouldContain(e => e.Contains("llm_judge requires rubric"));
        errors.ShouldContain(e => e.Contains("unknown type 'nope'"));
    }

    [Fact]
    public void Scorer_TaskScore_Weighted()
    {
        var asserts = new List<AssertResult>
        {
            new("exit_code", true, false, 3, null),
            new("file_contains", false, false, 1, null),
            new("llm_judge", false, true, 1, "skipped"), // fora do score
        };
        Scorer.TaskScore(asserts).ShouldBe(0.75, 0.001);
    }

    [Fact]
    public void Scorer_Regressions_Flags_Drop_And_PassToFail()
    {
        var baseline = new List<EvalResult>
        {
            new() { Name = "a", Score = 1, Pass = true },
            new() { Name = "b", Score = 0.9, Pass = true },
            new() { Name = "c", Score = 0.5, Pass = false },
        };
        var current = new List<EvalResult>
        {
            new() { Name = "a", Score = 1, Pass = true },
            new() { Name = "b", Score = 0.8, Pass = true },   // drop >5%
            new() { Name = "c", Score = 0.5, Pass = false },
        };
        var regs = Scorer.Regressions(current, baseline);
        regs.ShouldHaveSingleItem().Task.ShouldBe("b");
    }

    [Fact]
    public async Task Runner_Live_Path_Evaluates_Asserts()
    {
        var backend = new FakeBackend(
            events: [CmdOut("hello world")],
            execCodes: new() { ["true"] = 0 },
            files: new() { ["/workspace/a.txt"] = "needle here" });
        var runner = new EvalRunner(backend);
        var task = EvalParser.ParseTask("""
            name: t1
            task: do it
            assert:
              - type: exit_code
                cmd: "true"
                expect: 0
              - type: file_contains
                path: /workspace/a.txt
                pattern: "needle"
              - type: output_contains
                pattern: "hello world"
            """);
        var results = await runner.RunAsync([task],
            new EvalRunOptions(Parallel: 2));
        var r = results.ShouldHaveSingleItem();
        r.Pass.ShouldBeTrue();
        r.Score.ShouldBe(1);
        r.Mode.ShouldBe("live");
        r.ConversationId.ShouldBe("conv-1");
    }

    [Fact]
    public async Task Runner_Recorded_Replays_Without_Backend()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            $"eval-rec-{Guid.NewGuid():N}");
        var store = new RecordingStore(dir);
        var task = EvalParser.ParseTask("""
            name: rec
            task: recorded task
            assert:
              - type: exit_code
                cmd: "make x"
                expect: 0
              - type: output_contains
                pattern: "done"
            """);
        var evidence = new EvalEvidence("all done",
            new Dictionary<string, int> { ["make x"] = 0 },
            new Dictionary<string, string?>(),
            new Dictionary<string, int>(), 0);
        await store.SaveAsync(task, "conv-x", evidence);

        var runner = new EvalRunner(new FakeBackend(), store);
        var r = (await runner.RunAsync([task],
            new EvalRunOptions(Mode: "recorded"))).Single();
        r.Mode.ShouldBe("recorded");
        r.Pass.ShouldBeTrue();
        r.Score.ShouldBe(1);
        r.ConversationId.ShouldBe("conv-x");
    }

    [Fact]
    public async Task Runner_Recorded_Missing_Recording_Fails()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            $"eval-rec-{Guid.NewGuid():N}");
        var runner = new EvalRunner(new FakeBackend(),
            new RecordingStore(dir));
        var task = EvalParser.ParseTask(ValidTask);
        var r = (await runner.RunAsync([task],
            new EvalRunOptions(Mode: "recorded"))).Single();
        r.Pass.ShouldBeFalse();
        r.ExecutionStatus.ShouldBe("NO_RECORDING");
    }

    [Fact]
    public void ResultJson_RoundTrips()
    {
        var r = new EvalResult
        {
            Name = "x",
            Score = 0.5,
            Pass = false,
            Asserts = [new AssertResult("exit_code", true, false, 1, null)],
            Tokens = 123,
        };
        var json = EvalResult.ToJson(r);
        json.ShouldContain("\"score\": 0.5");
        var back = EvalResult.FromJson(json);
        back!.Name.ShouldBe("x");
        back.Score.ShouldBe(0.5);
        back.Tokens.ShouldBe(123);
    }

    private static JsonElement CmdOut(string text) =>
        JsonDocument.Parse(
            $"{{\"kind\":\"command_output\",\"id\":\"e1\",\"text\":\"{text}\"}}")
            .RootElement.Clone();

    private sealed class FakeBackend(
        IReadOnlyList<JsonElement>? events = null,
        Dictionary<string, int>? execCodes = null,
        Dictionary<string, string?>? files = null) : IEvalBackend
    {
        public Task<string> CreateConversationAsync(
            string t, string? r, string? m, CancellationToken ct) =>
            Task.FromResult("task-1");
        public Task<string> WaitReadyAsync(string id, CancellationToken ct) =>
            Task.FromResult("conv-1");
        public Task RunSetupAsync(
            string c, IReadOnlyList<string> cmds, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<JsonElement>> WatchAsync(
            string c, TimeSpan t, CancellationToken ct) =>
            Task.FromResult(events ?? (IReadOnlyList<JsonElement>)[]);
        public Task<(int ExitCode, string StdOut)> ExecAsync(
            string c, string cmd, CancellationToken ct) =>
            Task.FromResult(
                (execCodes?.GetValueOrDefault(cmd, -1) ?? -1, ""));
        public Task<string?> ReadFileAsync(
            string c, string p, CancellationToken ct) =>
            Task.FromResult(files?.GetValueOrDefault(p));
        public Task<(double Score, string Rationale)?> JudgeAsync(
            string r, string t, CancellationToken ct) =>
            Task.FromResult<(double, string)?>(null);
    }
}