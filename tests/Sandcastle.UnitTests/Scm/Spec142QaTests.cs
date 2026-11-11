using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Artifacts;
using Sandcastle.Application.Git;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Sandbox;
using Sandcastle.Application.Scm;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Scm;
using Sandcastle.Infrastructure.Scm.Qa;

using Shouldly;

using Xunit;

using SandboxModel = Sandcastle.Domain.Sandbox.Sandbox;

namespace Sandcastle.UnitTests.Scm;

/// <summary>
/// SPEC-142 — QA agent com evidência: roteamento do webhook
/// (<c>pull_request</c> opened/synchronize → trigger com dedup por
/// head, enrollment <c>qa_enabled</c>/<c>qa_repos</c>, anti-loop
/// estrutural), <see cref="QaService"/> (dedup, quota, manual trigger,
/// listagem, detalhe), <see cref="QaPlanParser"/> (classificação,
/// passos, reprodução), <see cref="QaFormatter.CommitStatus"/>
/// (advisory: completed|passed), veredicto honesto
/// (<see cref="QaRunner.Verdict"/>) e o pipeline
/// <see cref="QaRunner"/> de ponta a ponta com fakes de provider, LLM,
/// sandbox, git, driver browser e artifact store.
/// </summary>
public sealed class Spec142QaTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly Guid _orgId = Guid.NewGuid();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly FakeProvider _provider = new();
    private readonly FakeQaDispatcher _dispatcher = new();
    private readonly CapturingAudit _audit = new();
    private readonly FakeSandboxes _sandboxes = new();
    private readonly FakeExecutor _executor = new();
    private readonly FakeGit _git = new();
    private readonly FakeArtifacts _artifacts = new();
    private readonly FakeDriver _driver = new();
    private readonly FakeLlm _llm = new();
    private readonly FakeSecrets _secrets = new();
    private readonly FakeMeter _meter = new();

    public Spec142QaTests()
    {
        _conn.Open();
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
        _db.ScmConnections.Add(new ScmConnectionRecord
        {
            Id = _connectionId,
            OrgId = _orgId,
            Kind = "github",
            InstallationId = "777",
            PemSecretRef = "pem_ref",
            WebhookSecretRef = "hook_ref",
            AppSlug = "sandcastle-agent",
            QaEnabled = true,
            CreatedByUserId = _ownerId.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    // ---- helpers ----------------------------------------------------------

    private QaService Service(QaOptions? options = null) =>
        new(_db, _dispatcher, new FakeRegistry(_provider),
            Options.Create(options ?? new QaOptions()),
            NullLogger<QaService>.Instance);

    private QaRunner Runner(QaOptions? options = null) =>
        new(_db, new FakeRegistry(_provider), _llm, new FakeRouter(),
            Options.Create(options ?? new QaOptions
            {
                StepTimeoutSeconds = 5,
                AppReadySeconds = 4,
            }),
            _sandboxes, _executor, _git, new FakeClientFactory(),
            new FakeDriverFactory(_driver), _artifacts, _meter, _audit,
            NullLogger<QaRunner>.Instance);

    private GitHubWebhookService Webhook(IQaService? qa = null) =>
        new(_db, _secrets,
            new FakeConversations(), _audit,
            NullLogger<GitHubWebhookService>.Instance,
            reviews: null, reviewOptions: null,
            autofix: null, autofixOptions: null,
            qa: qa ?? Service(),
            qaOptions: Options.Create(new QaOptions()));

    private ScmConnection Connection(bool qaEnabled = true,
        string[]? qaRepos = null, string? pass = null) =>
        new(_connectionId, _orgId, ScmKind.GitHub, "777", "pem_ref",
            "hook_ref", [], "42", "sandcastle-agent",
            _ownerId.ToString(), DateTimeOffset.UtcNow,
            QaEnabled: qaEnabled, QaRepos: qaRepos,
            QaPassCondition: pass);

    private static string Sign(byte[] payload)
    {
        var hex = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("hooksecret"), payload));
        return $"sha256={hex}";
    }

    private static byte[] PrPayload(
        string action = "opened", string headSha = "head-1",
        string baseSha = "base-1", int pr = 7, bool draft = false,
        string repo = "afonsoft/demo") =>
        Encoding.UTF8.GetBytes(
            "{\"action\":\"" + action + "\","
            + "\"installation\":{\"id\":777},"
            + "\"repository\":{\"full_name\":\"" + repo + "\"},"
            + "\"sender\":{\"login\":\"octocat\"},"
            + "\"pull_request\":{\"number\":" + pr + ","
            + "\"draft\":" + (draft ? "true" : "false") + ","
            + "\"title\":\"demo pr\","
            + "\"head\":{\"sha\":\"" + headSha + "\","
            + "\"repo\":{\"full_name\":\"" + repo + "\"}},"
            + "\"base\":{\"sha\":\"" + baseSha + "\"}}}");

    private async Task<QaRunRecord> SeedRunAsync(
        string status = QaRunStatuses.Queued, string headSha = "head-1",
        int pr = 7)
    {
        var run = new QaRunRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            ConnectionId = _connectionId,
            Repository = "afonsoft/demo",
            PrNumber = pr,
            HeadSha = headSha,
            Status = status,
            Trigger = "webhook",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.QaRuns.Add(run);
        await _db.SaveChangesAsync();
        return run;
    }

    /// <summary>Plano JSON padrão devolvido pelo LLM fake.</summary>
    private static string PlanJson(
        string classification = "feature", int execSteps = 2) =>
        $$"""
        {"classification":"{{classification}}","summary":"demo",
         "start":{"command":"npm run dev","port":3000,"path":"/"},
         "steps":[
           {{string.Join(",",
               Enumerable.Range(1, execSteps).Select(i =>
                   $"{{\"kind\":\"exec\",\"name\":\"check {i}\"," +
                   $"\"command\":\"curl -sf /api/{i}\"}}"))}},
           {"kind":"browser","name":"load home","action":"goto","url":"/"}
         ]}
        """;

    private void HappyPathExecutor()
    {
        _executor.Handler = cmd => cmd switch
        {
            _ when cmd.Contains("nohup bash") || cmd.Contains("kill -0")
                || cmd.Contains("mkdir -p") || cmd.Contains("cat ")
                || cmd.Contains("ls -1") || cmd.Contains("printf")
                || cmd.Contains("base64")
                => new CommandResult(0, "", "", false),
            _ when cmd.Contains("git -C /workspace/repo")
                => new CommandResult(0, "", "", false),
            _ when cmd.Contains("curl -sf -o /dev/null")
                => new CommandResult(0, "", "", false),
            _ => new CommandResult(0, "ok", "", false),
        };
    }

    // ---- RF-001: webhook routing ------------------------------------------

    [Fact]
    public async Task Webhook_pr_opened_enqueues_qa_run()
    {
        var payload = PrPayload();
        var result = await Webhook().HandleGitHubAsync(
            Sign(payload), "pull_request", "del-1", payload,
            CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.Processed);
        _dispatcher.Enqueued.ShouldHaveSingleItem();
        var run = await _db.QaRuns.SingleAsync();
        run.Repository.ShouldBe("afonsoft/demo");
        run.PrNumber.ShouldBe(7);
        run.HeadSha.ShouldBe("head-1");
        run.BaseSha.ShouldBe("base-1");
        run.Status.ShouldBe(QaRunStatuses.Queued);
        _audit.Actions.ShouldContain("scm.qa_triggered");
    }

    [Fact]
    public async Task Webhook_synchronize_new_head_enqueues_second_run()
    {
        var first = PrPayload(headSha: "head-1");
        await Webhook().HandleGitHubAsync(
            Sign(first), "pull_request", "d1", first,
            CancellationToken.None);
        var second = PrPayload(
            action: "synchronize", headSha: "head-2");
        var result = await Webhook().HandleGitHubAsync(
            Sign(second), "pull_request", "d2", second,
            CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.Processed);
        (await _db.QaRuns.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Webhook_same_head_is_deduped()
    {
        var payload = PrPayload();
        await Webhook().HandleGitHubAsync(
            Sign(payload), "pull_request", "d1", payload,
            CancellationToken.None);
        var result = await Webhook().HandleGitHubAsync(
            Sign(payload), "pull_request", "d2", payload,
            CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.Ignored);
        (await _db.QaRuns.CountAsync()).ShouldBe(1);
        _dispatcher.Enqueued.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Webhook_ignored_when_qa_disabled()
    {
        var conn = Connection(qaEnabled: false);
        // connection seed tem QaEnabled=true — flip via service real:
        var svc = new ScmConnectionService(_db, _secrets, _audit,
            new FakeHttp(), new FakeRegistry(_provider),
            NullLogger<ScmConnectionService>.Instance);
        await svc.UpdateQaSettingsAsync(_orgId, _connectionId,
            new ScmConnectionQaUpdate(QaEnabled: false, null, null),
            CancellationToken.None);
        var payload = PrPayload();
        var result = await Webhook().HandleGitHubAsync(
            Sign(payload), "pull_request", "d1", payload,
            CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.Ignored);
        (await _db.QaRuns.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Webhook_ignored_for_labeled_and_other_actions()
    {
        foreach (var action in new[] { "labeled", "closed", "edited",
                 "review_requested", "assigned" })
        {
            var payload = PrPayload(action: action);
            var result = await Webhook().HandleGitHubAsync(
                Sign(payload), "pull_request", $"d-{action}", payload,
                CancellationToken.None);
            result.ShouldBe(ScmWebhookResult.Ignored,
                $"action {action} should not trigger QA");
        }
        (await _db.QaRuns.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Webhook_ignored_for_draft_pr()
    {
        var payload = PrPayload(draft: true);
        var result = await Webhook().HandleGitHubAsync(
            Sign(payload), "pull_request", "d1", payload,
            CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.Ignored);
    }

    [Fact]
    public async Task Webhook_ignored_for_repo_outside_qa_repos()
    {
        var svc = new ScmConnectionService(_db, _secrets, _audit,
            new FakeHttp(), new FakeRegistry(_provider),
            NullLogger<ScmConnectionService>.Instance);
        await svc.UpdateQaSettingsAsync(_orgId, _connectionId,
            new ScmConnectionQaUpdate(
                null, ["afonsoft/other"], null),
            CancellationToken.None);
        var payload = PrPayload();
        var result = await Webhook().HandleGitHubAsync(
            Sign(payload), "pull_request", "d1", payload,
            CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.Ignored);
    }

    // ---- RF-001: QaService trigger/dedup/quota/manual ---------------------

    [Fact]
    public async Task Trigger_dedups_by_repo_pr_head()
    {
        var conn = Connection();
        var first = await Service().TriggerAsync(
            conn, "afonsoft/demo", 7, "sha-1", null, "manual", "u1",
            CancellationToken.None);
        first.QaRunId.ShouldNotBeNull();
        var second = await Service().TriggerAsync(
            conn, "afonsoft/demo", 7, "sha-1", null, "manual", "u1",
            CancellationToken.None);
        second.Error.ShouldBe(QaTriggerError.Duplicate);
        var other = await Service().TriggerAsync(
            conn, "afonsoft/demo", 7, "sha-2", null, "manual", "u1",
            CancellationToken.None);
        other.QaRunId.ShouldNotBeNull();
    }

    [Fact]
    public async Task Trigger_enforces_daily_quota()
    {
        var svc = Service(new QaOptions { MaxDailyRunsPerRepo = 2 });
        var conn = Connection();
        for (var i = 0; i < 2; i++)
        {
            (await svc.TriggerAsync(conn, "afonsoft/demo", i + 1,
                $"sha-{i}", null, "manual", null,
                CancellationToken.None)).QaRunId.ShouldNotBeNull();
        }
        var result = await svc.TriggerAsync(conn, "afonsoft/demo", 9,
            "sha-9", null, "manual", null, CancellationToken.None);
        result.Error.ShouldBe(QaTriggerError.QuotaExceeded);
    }

    [Fact]
    public async Task Manual_trigger_requires_qa_enrollment()
    {
        var conn = Connection(qaEnabled: false);
        var result = await Service().TriggerManualAsync(
            conn, "afonsoft/demo", 7, "u1", CancellationToken.None);
        result.Error.ShouldBe(QaTriggerError.Disabled);
    }

    [Fact]
    public async Task Manual_trigger_resolves_head_from_provider()
    {
        var result = await Service().TriggerManualAsync(
            Connection(), "afonsoft/demo", 7, "u1",
            CancellationToken.None);
        result.QaRunId.ShouldNotBeNull();
        var run = await _db.QaRuns.SingleAsync();
        run.HeadSha.ShouldBe("head-1");
        run.Trigger.ShouldBe("manual");
        run.RequestedByUserId.ShouldBe("u1");
        _dispatcher.Enqueued.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Manual_trigger_404_when_pr_missing()
    {
        _provider.PullRequest = null;
        var result = await Service().TriggerManualAsync(
            Connection(), "afonsoft/demo", 99, "u1",
            CancellationToken.None);
        result.Error.ShouldBe(QaTriggerError.NotFound);
    }

    // ---- RF-002: plan parser ----------------------------------------------

    [Fact]
    public void Plan_parser_extracts_classification_and_steps()
    {
        var plan = QaPlanParser.Parse(PlanJson("bugfix"), 30)!;
        plan.Classification.ShouldBe(QaClassifications.Bugfix);
        plan.Start!.Port.ShouldBe(3000);
        plan.Steps.Count.ShouldBe(3);
        plan.Steps[0].Kind.ShouldBe("exec");
        plan.Steps[2].Kind.ShouldBe("browser");
    }

    [Fact]
    public void Plan_parser_normalizes_classification_aliases()
    {
        QaPlanParser.NormalizeClassification("fix")
            .ShouldBe(QaClassifications.Bugfix);
        QaPlanParser.NormalizeClassification("FEAT")
            .ShouldBe(QaClassifications.Feature);
        QaPlanParser.NormalizeClassification("docs")
            .ShouldBe(QaClassifications.Config);
        QaPlanParser.NormalizeClassification("whatever")
            .ShouldBe(QaClassifications.Unknown);
        QaPlanParser.NormalizeClassification(null)
            .ShouldBe(QaClassifications.Unknown);
    }

    [Fact]
    public void Plan_parser_rejects_garbage()
    {
        QaPlanParser.Parse("no json here", 30).ShouldBeNull();
        QaPlanParser.Parse("{}", 30).ShouldBeNull();
        QaPlanParser.Parse("{\"steps\":[]}", 30).ShouldBeNull();
        QaPlanParser.Parse(null, 30).ShouldBeNull();
    }

    [Fact]
    public void Plan_parser_caps_steps_at_max()
    {
        var plan = QaPlanParser.Parse(PlanJson(execSteps: 20), 5)!;
        plan.Steps.Count.ShouldBeLessThanOrEqualTo(5);
    }

    [Fact]
    public void Plan_parser_drops_invalid_steps()
    {
        const string json = """
        {"classification":"feature",
         "steps":[
           {"kind":"exec","command":"make test"},
           {"kind":"exec"},
           {"kind":"browser","action":"goto"},
           {"kind":"browser","action":"fly","url":"/x"},
           {"kind":"browser","action":"goto","url":"/ok"}
         ]}
        """;
        var plan = QaPlanParser.Parse(json, 30)!;
        plan.Steps.Count.ShouldBe(2);
        plan.Steps[0].Command.ShouldBe("make test");
        plan.Steps[1].Url.ShouldBe("/ok");
    }

    [Fact]
    public void Plan_parser_extracts_reproduction()
    {
        const string json = """
        {"classification":"bugfix","steps":[
           {"kind":"exec","command":"pytest -x"}],
         "reproduction":{"command":"curl /api/bug",
            "indicator":"contains:Error"}}
        """;
        var plan = QaPlanParser.Parse(json, 30)!;
        plan.Reproduction!.Command.ShouldBe("curl /api/bug");
        plan.Reproduction.Indicator.ShouldBe("contains:Error");
    }

    // ---- RF-002/RF-004: verdict + status logic ----------------------------

    [Fact]
    public void Verdict_pass_when_all_test_steps_ok()
    {
        var steps = new List<QaStepRecord>
        {
            Step("clone", QaStepOutcomes.Ok),
            Step("exec", QaStepOutcomes.Ok),
            Step("browser", QaStepOutcomes.Ok),
        };
        QaRunner.Verdict(steps).ShouldBe(QaVerdicts.Pass);
    }

    [Fact]
    public void Verdict_fail_when_test_step_fails()
    {
        var steps = new List<QaStepRecord>
        {
            Step("exec", QaStepOutcomes.Ok),
            Step("browser", QaStepOutcomes.Fail),
        };
        QaRunner.Verdict(steps).ShouldBe(QaVerdicts.Fail);
    }

    [Fact]
    public void Verdict_partial_when_step_skipped_or_infra_failed()
    {
        QaRunner.Verdict(
        [
            Step("exec", QaStepOutcomes.Ok),
            Step("exec", QaStepOutcomes.Skipped),
        ]).ShouldBe(QaVerdicts.Partial);
        QaRunner.Verdict(
        [
            Step("clone", QaStepOutcomes.InfraFail),
            Step("exec", QaStepOutcomes.Ok),
        ]).ShouldBe(QaVerdicts.Partial);
        QaRunner.Verdict([]).ShouldBe(QaVerdicts.Partial);
    }

    [Theory]
    [InlineData("completed", "pass", "success")]
    [InlineData("completed", "fail", "success")]
    [InlineData("completed", "partial", "success")]
    [InlineData("passed", "pass", "success")]
    [InlineData("passed", "fail", "failure")]
    [InlineData("passed", "partial", "failure")]
    public void Commit_status_maps_verdict_per_pass_condition(
        string condition, string verdict, string expected)
    {
        var (state, _) = QaFormatter.CommitStatus(
            condition, verdict, runFailed: false);
        state.ShouldBe(expected);
    }

    [Fact]
    public void Commit_status_error_when_pipeline_failed() =>
        QaFormatter.CommitStatus("completed", null, runFailed: true)
            .State.ShouldBe("error");

    // ---- RF-003/RF-005: runner pipeline (fakes) -----------------------------

    [Fact]
    public async Task Runner_completes_pass_and_posts_comment_and_status()
    {
        HappyPathExecutor();
        _llm.Script = PlanJson();
        _driver.Result = new QaBrowserRunResult(true, null,
        [
            new QaBrowserStepResult(true, "http://localhost:3000/",
                [1, 2, 3], "home"),
        ], [9, 9]);
        var run = await SeedRunAsync();

        await Runner().RunAsync(run.Id, CancellationToken.None);

        var done = await _db.QaRuns.SingleAsync(r => r.Id == run.Id);
        done.Status.ShouldBe(QaRunStatuses.Completed);
        done.Verdict.ShouldBe(QaVerdicts.Pass);
        done.Classification.ShouldBe(QaClassifications.Feature);
        done.PlanJson.ShouldNotBeNull();
        _provider.IssueComments.ShouldHaveSingleItem();
        _provider.Statuses.ShouldHaveSingleItem();
        _provider.Statuses[0].Context.ShouldBe(QaDefaults.StatusContext);
        _provider.Statuses[0].State.ShouldBe("success");
        _audit.Actions.ShouldContain("scm.qa_completed");
        // screenshots + vídeo persistidos como artifacts do run.
        _artifacts.Puts.Count.ShouldBe(2);
        var steps = await _db.QaSteps
            .Where(s => s.QaRunId == run.Id).ToListAsync();
        steps.ShouldContain(
            s => s.Action == "browser" && s.ArtifactRef != null);
        steps.ShouldContain(
            s => s.Action == "video" && s.ArtifactRef != null);
        _sandboxes.Deleted.ShouldContain(_sandboxes.LastStarted!.Id);
    }

    [Fact]
    public async Task Runner_fails_verdict_on_failing_exec_step()
    {
        HappyPathExecutor();
        _executor.Handler = cmd =>
            cmd.Contains("curl -sf /api/1")
                ? new CommandResult(1, "", "boom", false)
                : HappyHandler(cmd);
        _llm.Script = PlanJson();
        var run = await SeedRunAsync();

        await Runner().RunAsync(run.Id, CancellationToken.None);

        var done = await _db.QaRuns.SingleAsync(r => r.Id == run.Id);
        done.Verdict.ShouldBe(QaVerdicts.Fail);
        (await _db.QaSteps.AnyAsync(s => s.QaRunId == run.Id
            && s.Outcome == QaStepOutcomes.Fail)).ShouldBeTrue();
    }

    [Fact]
    public async Task Runner_partial_when_browser_unavailable_honest_bail()
    {
        HappyPathExecutor();
        _llm.Script = PlanJson();
        _driver.Result = new QaBrowserRunResult(
            false, "playwright not installed", [], null);
        var run = await SeedRunAsync();

        await Runner().RunAsync(run.Id, CancellationToken.None);

        var done = await _db.QaRuns.SingleAsync(r => r.Id == run.Id);
        done.Verdict.ShouldBe(QaVerdicts.Partial); // honesto — nunca PASS
        var browser = await _db.QaSteps.SingleAsync(
            s => s.QaRunId == run.Id && s.Action == "browser");
        browser.Outcome.ShouldBe(QaStepOutcomes.Skipped);
    }

    [Fact]
    public async Task Runner_partial_when_app_never_ready()
    {
        HappyPathExecutor();
        _executor.Handler = cmd =>
            cmd.Contains("curl -sf -o /dev/null")
                ? new CommandResult(7, "", "", false)
                : HappyHandler(cmd);
        _llm.Script = PlanJson();
        var run = await SeedRunAsync();

        await Runner().RunAsync(run.Id, CancellationToken.None);

        var done = await _db.QaRuns.SingleAsync(r => r.Id == run.Id);
        done.Verdict.ShouldBe(QaVerdicts.Partial);
        (await _db.QaSteps.AnyAsync(s => s.QaRunId == run.Id
            && s.Outcome == QaStepOutcomes.Skipped)).ShouldBeFalse();
        // app_start falha de infra → PARTIAL; passos de teste não rodam.
        (await _db.QaSteps.AnyAsync(s => s.QaRunId == run.Id
            && s.Action == "app_start"
            && s.Outcome == QaStepOutcomes.InfraFail)).ShouldBeTrue();
    }

    [Fact]
    public async Task Runner_partial_when_llm_gives_no_plan()
    {
        _llm.Script = "sorry, cannot plan this";
        var run = await SeedRunAsync();

        await Runner().RunAsync(run.Id, CancellationToken.None);

        var done = await _db.QaRuns.SingleAsync(r => r.Id == run.Id);
        done.Status.ShouldBe(QaRunStatuses.Completed);
        done.Verdict.ShouldBe(QaVerdicts.Partial);
        _sandboxes.LastStarted.ShouldBeNull(); // nem provisiona sandbox
        _provider.IssueComments.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Runner_is_idempotent_on_terminal_runs()
    {
        var run = await SeedRunAsync(
            status: QaRunStatuses.Completed);
        await Runner().RunAsync(run.Id, CancellationToken.None);
        _provider.IssueComments.ShouldBeEmpty();
    }

    [Fact]
    public async Task Runner_records_step_durations_and_numbers()
    {
        HappyPathExecutor();
        _llm.Script = PlanJson();
        var run = await SeedRunAsync();

        await Runner().RunAsync(run.Id, CancellationToken.None);

        var steps = await _db.QaSteps
            .Where(s => s.QaRunId == run.Id)
            .OrderBy(s => s.Number).ToListAsync();
        steps.Count.ShouldBeGreaterThan(3);
        steps.Select(s => s.Number).ShouldBe(
            Enumerable.Range(1, steps.Count));
        steps.ShouldAllBe(s => s.DurationMs != null);
    }

    [Fact]
    public async Task Runner_captures_artifacts_under_run_namespace()
    {
        HappyPathExecutor();
        _llm.Script = PlanJson();
        _driver.Result = new QaBrowserRunResult(true, null,
            [new QaBrowserStepResult(true, "url", [4, 5], null)],
            null);
        var run = await SeedRunAsync(); // sem conversa → ns = run.Id

        await Runner().RunAsync(run.Id, CancellationToken.None);

        _artifacts.Puts.ShouldHaveSingleItem();
        _artifacts.Puts[0].ConversationId.ShouldBe(run.Id);
        _artifacts.Puts[0].Name.ShouldEndWith(".png");
    }

    // ---- listagem/detalhe --------------------------------------------------

    [Fact]
    public async Task Service_searches_and_gets_run_detail_with_evidence()
    {
        var run = await SeedRunAsync();
        _db.QaSteps.Add(new QaStepRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            QaRunId = run.Id,
            Number = 1,
            Action = "browser",
            Outcome = QaStepOutcomes.Ok,
            ArtifactRef = "ws:x/step.png",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();

        var page = await Service().SearchAsync(
            _orgId, "afonsoft/demo", 7, null, null, 50,
            CancellationToken.None);
        page.Items.ShouldHaveSingleItem();
        page.Items[0].StepCount.ShouldBe(1);

        var detail = await Service().GetAsync(
            _orgId, run.Id, CancellationToken.None)!;
        detail!.Run.Id.ShouldBe(run.Id);
        detail.Steps.ShouldHaveSingleItem();
        detail.Evidence.ShouldHaveSingleItem();
        detail.Evidence[0].Url.ShouldStartWith(
            $"/api/v1/qa/runs/{run.Id}/artifacts/");

        (await Service().GetAsync(
            Guid.NewGuid(), run.Id, CancellationToken.None))
            .ShouldBeNull(); // outra org — ACL
    }

    // ---- internals --------------------------------------------------------

    private static QaStepRecord Step(string action, string outcome) =>
        new()
        {
            Id = Guid.NewGuid(),
            QaRunId = Guid.NewGuid(),
            Number = 1,
            Action = action,
            Outcome = outcome,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private CommandResult HappyHandler(string cmd) => cmd switch
    {
        _ when cmd.Contains("curl -sf -o /dev/null")
            => new CommandResult(0, "", "", false),
        _ => new CommandResult(0, "ok", "", false),
    };

    // ---- fakes -------------------------------------------------------------

    private sealed class FakeProvider : IScmProvider
    {
        public ScmPullRequest? PullRequest = new(
            "https://gh/pr/7", 7, "open", "head-1", "demo pr");
        public IReadOnlyList<ScmPullRequestFile> Files =
        [
            new ScmPullRequestFile("src/App.cs", null, "modified",
                10, 2, "@@ -1,2 +1,3 @@\n ctx\n+new line\n ctx"),
        ];
        public List<(int Number, string Body)> IssueComments = [];
        public List<(string Sha, string State, string Context,
            string? Description)> Statuses = [];

        public ScmKind Kind => ScmKind.GitHub;

        public Task<ScmPullRequest?> GetPullRequestAsync(
            ScmConnection c, string r, int n, CancellationToken ct) =>
            Task.FromResult(PullRequest);

        public Task<IReadOnlyList<ScmPullRequestFile>>
            GetPullRequestFilesAsync(ScmConnection c, string r, int n,
                CancellationToken ct) => Task.FromResult(Files);

        public Task<long> CreateIssueCommentAsync(
            ScmConnection c, string r, int n, string b,
            CancellationToken ct)
        {
            IssueComments.Add((n, b));
            return Task.FromResult(1L);
        }

        public Task SetCommitStatusAsync(ScmConnection c, string r,
            string s, string st, string ctx, string? t, string? d,
            CancellationToken ct)
        {
            Statuses.Add((s, st, ctx, d));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ScmCheckRun>> GetCheckRunsAsync(
            ScmConnection c, string r, string rf,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<string?> GetCheckRunLogsAsync(
            ScmConnection c, string r, long i,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<string?> GetWorkflowRunLogsAsync(
            ScmConnection c, string r, long i,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<IReadOnlyList<ScmCommitPullRequest>>
            GetPullRequestsForCommitAsync(ScmConnection c, string r,
                string s, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<long> ReplyToReviewCommentAsync(ScmConnection c,
            string r, int p, long i, string b, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<ScmPullRequest> CreatePullRequestAsync(
            ScmConnection c, string r, ScmPrCreateRequest q,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<ScmConnectionDiagnostic> TestConnectionAsync(
            ScmConnection c, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<string?> GetFileContentAsync(ScmConnection c,
            string r, string p, string? rf, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<string>> ListDirectoryAsync(
            ScmConnection c, string r, string p, string? rf,
            CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<ScmReviewResult?> CreateReviewAsync(
            ScmConnection c, string r, int n, string h, string b,
            IReadOnlyList<ScmReviewComment> cs,
            CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeRegistry(IScmProvider provider)
        : IScmProviderRegistry
    {
        public IScmProvider? Get(ScmKind kind) =>
            kind == ScmKind.GitHub ? provider : null;
    }

    private sealed class FakeQaDispatcher : IQaDispatcher
    {
        public List<Guid> Enqueued = [];

        public ValueTask EnqueueAsync(Guid qaRunId, CancellationToken ct)
        {
            Enqueued.Add(qaRunId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingAudit : IAuditService
    {
        public List<string> Actions { get; } = [];

        public Task RecordAsync(string action, string? actorUserId = null,
            Guid? orgId = null, string? targetType = null,
            string? targetId = null, string? ip = null,
            string? userAgent = null, object? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }

        public Task<(IReadOnlyList<AuditEntry> Items, int Total)> QueryAsync(
            string? userId, string? action, DateTimeOffset? from,
            DateTimeOffset? to, int page, int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<AuditEntry>, int)>(([], 0));
    }

    private sealed class FakeSandboxes : ISandboxService
    {
        public SandboxModel? LastStarted;
        public List<Guid> Deleted = [];

        public Task<SandboxModel> StartAsync(Guid specId,
            Guid? conversationId, CancellationToken ct)
        {
            LastStarted = new SandboxModel(
                Guid.NewGuid(), specId, conversationId, "ctr-1",
                SandboxStatus.Running, null,
                DateTimeOffset.UtcNow, null, null);
            return Task.FromResult(LastStarted);
        }

        public Task DeleteAsync(Guid sandboxId, CancellationToken ct)
        {
            Deleted.Add(sandboxId);
            return Task.CompletedTask;
        }

        public Task<SandboxModel?> GetAsync(Guid sandboxId,
            CancellationToken ct) => Task.FromResult(LastStarted);

        public Task<SandboxModel> PauseAsync(Guid id,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<SandboxModel> StopAsync(Guid id,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<SandboxModel> ResumeAsync(Guid id,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<IReadOnlyList<SandboxModel>> SearchAsync(
            SandboxStatus? status, Guid? conversationId,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<SandboxSpec> UpsertSpecAsync(SandboxSpec spec,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<SandboxSpec?> GetSpecAsync(Guid specId,
            CancellationToken ct) => throw new NotImplementedException();

        public Task SweepOrphansAsync(CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeExecutor : ISandboxExecutor
    {
        public Func<string, CommandResult> Handler =
            _ => new CommandResult(0, "", "", false);
        public List<string> Commands = [];

        public Task<CommandResult> ExecAsync(SandboxModel sandbox,
            string command, TimeSpan timeout, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(Handler(command));
        }

        public Task<IBashSession> OpenBashSessionAsync(
            SandboxModel sandbox, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<string> ReadFileAsync(SandboxModel sandbox,
            string path, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task WriteFileAsync(SandboxModel sandbox, string path,
            string content, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<FileEntry>> ListFilesAsync(
            SandboxModel sandbox, string path, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<GitDiff> GetGitDiffAsync(SandboxModel sandbox,
            CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeGit : IGitService
    {
        public bool TokenProvisioned;

        public Task<bool> ProvisionTokenAuthAsync(SandboxModel sandbox,
            string host, string token, CancellationToken ct)
        {
            TokenProvisioned = true;
            return Task.FromResult(true);
        }

        public Task CloneAsync(SandboxModel sandbox, string url,
            string branch, string? owner, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<bool> ProvisionGhAuthAsync(SandboxModel sandbox,
            string? ownerId, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<bool> HasRepositoryAsync(SandboxModel sandbox,
            CancellationToken ct) => Task.FromResult(true);

        public Task<GitPushResult> PushAsync(SandboxModel sandbox,
            string head, string? remoteBranch, bool forceWithLease,
            string? ownerId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<string?> GetCurrentBranchAsync(SandboxModel sandbox,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<GitChanges> GetChangesAsync(SandboxModel sandbox,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<GitDiffReport?> GetDiffAsync(SandboxModel sandbox,
            string? path, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<string?> GetRawDiffAsync(SandboxModel sandbox,
            string? path, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<GitLogEntry>> GetLogAsync(
            SandboxModel sandbox, int limit, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<string?> GetFileAtRefAsync(SandboxModel sandbox,
            string path, string reference, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<GitCommitResult> CommitAsync(SandboxModel sandbox,
            string message, IReadOnlyList<string>? files,
            CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeClientFactory : IGitHubClientFactory
    {
        public Task<Octokit.IGitHubClient> ForConnectionAsync(
            ScmConnection connection, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<string> GetInstallationTokenAsync(
            ScmConnection connection, CancellationToken ct) =>
            Task.FromResult("install-token");
    }

    private sealed class FakeArtifacts : IArtifactStore
    {
        public List<(Guid ConversationId, string Name, byte[] Data)>
            Puts = [];

        public Task<string> PutAsync(Guid conversationId, string name,
            ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            Puts.Add((conversationId, name, data.ToArray()));
            return Task.FromResult($"ws:{conversationId}/{name}");
        }

        public Task<Stream?> OpenAsync(Guid conversationId,
            string artifactRef, CancellationToken ct) =>
            Task.FromResult<Stream?>(
                new MemoryStream([1, 2, 3]));

        public Task DeleteConversationAsync(Guid conversationId,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeDriver : IQaBrowserDriver
    {
        public QaBrowserRunResult Result = new(
            true, null,
            [new QaBrowserStepResult(true, "ok", null, null)], null);
        public List<string> Batches = [];

        public Task<QaBrowserRunResult> RunStepsAsync(string baseUrl,
            IReadOnlyList<QaPlanStep> steps, CancellationToken ct)
        {
            Batches.Add(baseUrl);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeDriverFactory(FakeDriver driver)
        : IQaBrowserDriverFactory
    {
        public IQaBrowserDriver Create(SandboxModel sandbox) => driver;
    }

    private sealed class FakeLlm : IAgentLlmProvider
    {
        public string Script = "{}";
        public string ModelId => "mock-llm";

        public async IAsyncEnumerable<ILlmStreamChunk> ChatStreamAsync(
            IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<ToolSchema> tools, LlmOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct)
        {
            yield return new TextDelta(Script);
            yield return new CompletionEnd(
                new LlmUsage(10, 20, null), "stop", "r-1");
            await Task.CompletedTask;
        }

        public Task<ModelInfo> GetModelInfoAsync(CancellationToken ct) =>
            Task.FromResult(new ModelInfo("mock-llm", "mock"));
    }

    private sealed class FakeRouter : IModelRouter
    {
        public Task<ModelSelection> SelectAsync(Guid conversationId,
            ModelRole role, string? preferredModel,
            CancellationToken ct) =>
            Task.FromResult(new ModelSelection(
                "qa-model", "mock", null, 0, null));

        public Task<ModelSelection?> NextFallbackAsync(
            ModelSelection current, LlmErrorKind kind,
            CancellationToken ct) =>
            Task.FromResult<ModelSelection?>(null);
    }

    private sealed class FakeMeter : IUsageMeter
    {
        public long Tokens;

        public Task RecordTokensAsync(Guid orgId, long tokens,
            decimal? costUsd, CancellationToken ct)
        {
            Tokens += tokens;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class FakeSecrets
        : Sandcastle.Application.Contracts.ISecretStore
    {
        public Task<string?> ResolveAsync(string scope, string ownerId,
            string name, CancellationToken ct) =>
            Task.FromResult<string?>("hooksecret");

        public Task<IReadOnlyList<string>> ListNamesAsync(string scope,
            string ownerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task SetAsync(string scope, string ownerId, string name,
            string plaintext, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<bool> DeleteAsync(string scope, string ownerId,
            string name, CancellationToken ct) =>
            Task.FromResult(true);
    }

    private sealed class FakeConversations
        : Sandcastle.Application.Conversations.IAppConversationService
    {
        public Task<Sandcastle.Domain.Conversations.AppConversationStartTask>
            CreateForUserAsync(Guid userId, Guid? orgId,
                Sandcastle.Application.Conversations
                    .CreateConversationRequest request,
                CancellationToken ct) => throw new NotImplementedException();

        public Task<Sandcastle.Domain.Conversations.AppConversationStartTask>
            CreateAsync(Sandcastle.Application.Conversations
                .CreateConversationRequest request,
                CancellationToken ct) => throw new NotImplementedException();

        public Task<Sandcastle.Application.Conversations.ConversationPage>
            SearchAsync(Sandcastle.Application.Conversations
                .ConversationQuery query, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<long> CountAsync(Sandcastle.Application.Conversations
            .ConversationQuery query, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<Sandcastle.Domain.Conversations
            .AppConversation>> GetBatchAsync(
                IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<Sandcastle.Domain.Conversations.AppConversation?>
            GetAsync(Guid id, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<Sandcastle.Domain.Conversations.AppConversation?>
            UpdateAsync(Guid id, string? title,
                IReadOnlyList<string>? tags, bool? pinned,
                CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<Sandcastle.Application.Conversations.SendMessageError?>
            ValidateSendMessageAsync(Guid id, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<Sandcastle.Application.Conversations.SwitchResult>
            SwitchProfileAsync(Guid id, string? profile,
                CancellationToken ct) => throw new NotImplementedException();

        public Task<Sandcastle.Application.Conversations.SwitchResult>
            SwitchModelAsync(Guid id, string model,
                CancellationToken ct) => throw new NotImplementedException();

        public Task<Sandcastle.Domain.Conversations.AppConversation?>
            SetPermissionsAsync(Guid id,
                IReadOnlyList<Application.Agent.AgentPermissionRule>?
                    rules, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<Sandcastle.Domain.Conversations.AppConversation?>
            SetModeAsync(Guid id, string mode,
                CancellationToken ct) => throw new NotImplementedException();

        public Task<IReadOnlyList<Sandcastle.Application.Conversations
            .AttentionItem>> ListAttentionAsync(int limit,
                CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<Sandcastle.Application.Conversations
            .AttentionDismissResult> DismissAttentionAsync(Guid id,
                CancellationToken ct) =>
            throw new NotImplementedException();
    }
}