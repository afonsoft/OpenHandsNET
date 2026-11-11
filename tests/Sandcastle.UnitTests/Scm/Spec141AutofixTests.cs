using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Conversations;
using Sandcastle.Application.Git;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Sandbox;
using Sandcastle.Application.Scm;
using Sandcastle.Domain.Conversations;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Scm;
using Sandcastle.Infrastructure.Scm.Autofix;

using Shouldly;

using Xunit;

using SandboxModel = Sandcastle.Domain.Sandbox.Sandbox;

namespace Sandcastle.UnitTests.Scm;

/// <summary>
/// SPEC-141 — CI autofix loop: parser de eventos (check_run/
/// check_suite/workflow_run/status), roteamento do webhook, política do
/// <see cref="AutofixService"/> (dedup head+check, um run ativo por PR,
/// disabled flag, anti-loop por app/ator/head_sha_out, gate de
/// check-name, fork), classificador de falhas e o
/// <see cref="AutofixRunner"/> (green/cap/blocked/cancel) com provider,
/// conversas, sandboxes e git fakes.
/// </summary>
public sealed class Spec141AutofixTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly Guid _orgId = Guid.NewGuid();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly FakeProvider _provider = new();
    private readonly FakeAutofixDispatcher _dispatcher = new();
    private readonly CapturingAudit _audit = new();
    private readonly CapturingConversations _conversations = new();
    private readonly FakeSandboxes _sandboxes = new();
    private readonly FakeGit _git = new();
    private readonly FakeSecrets _secrets = new();

    public Spec141AutofixTests()
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
            AutofixEnabled = true,
            CreatedByUserId = _ownerId.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
    }

    // ---- helpers ----------------------------------------------------------

    private AutofixService Service(AutofixOptions? options = null) =>
        new(_db, _dispatcher, new FakeRegistry(_provider),
            Options.Create(options ?? new AutofixOptions()),
            NullLogger<AutofixService>.Instance);

    private AutofixRunner Runner(AutofixOptions? options = null) =>
        new(_db, new FakeRegistry(_provider), _conversations,
            _sandboxes, _git, new FakeClientFactory(),
            Options.Create(options ?? new AutofixOptions
            {
                PollSeconds = 1,
                RunTimeoutMinutes = 1,
            }),
            _audit, NullLogger<AutofixRunner>.Instance);

    private GitHubWebhookService Webhook(IAutofixService? autofix = null) =>
        new(_db, _secrets, _conversations, _audit,
            NullLogger<GitHubWebhookService>.Instance,
            reviews: null, reviewOptions: null,
            autofix: autofix ?? Service(),
            autofixOptions: Options.Create(new AutofixOptions()));

    private ScmConnection Connection(bool autofixEnabled = true) =>
        new(_connectionId, _orgId, ScmKind.GitHub, "777", "pem_ref",
            "hook_ref", [], "42", "sandcastle-agent",
            _ownerId.ToString(), DateTimeOffset.UtcNow,
            AutofixEnabled: autofixEnabled);

    private static string Sign(byte[] payload)
    {
        var hex = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("hooksecret"), payload));
        return $"sha256={hex}";
    }

    private static byte[] CheckRunPayload(
        string conclusion = "failure", string name = "build",
        string headSha = "head-1", int pr = 7) =>
        Encoding.UTF8.GetBytes(
            "{\"action\":\"completed\",\"installation\":{\"id\":777},"
            + "\"repository\":{\"full_name\":\"afonsoft/demo\"},"
            + "\"sender\":{\"login\":\"octocat\"},"
            + "\"check_run\":{\"id\":42,\"name\":\"" + name + "\","
            + "\"head_sha\":\"" + headSha + "\","
            + "\"conclusion\":\"" + conclusion + "\","
            + "\"details_url\":\"https://ci/run/42\","
            + "\"pull_requests\":[{\"number\":" + pr + ","
            + "\"head\":{\"ref\":\"fix/thing\","
            + "\"repo\":{\"full_name\":\"afonsoft/demo\"}}}]}}");

    private static byte[] WorkflowRunPayload(
        string conclusion = "failure", string name = "CI",
        string headSha = "head-1", string? actor = "octocat") =>
        Encoding.UTF8.GetBytes(
            "{\"action\":\"completed\",\"installation\":{\"id\":777},"
            + "\"repository\":{\"full_name\":\"afonsoft/demo\"},"
            + "\"sender\":{\"login\":\"" + (actor ?? "octocat") + "\"},"
            + "\"workflow_run\":{\"id\":99,\"name\":\"" + name + "\","
            + "\"head_sha\":\"" + headSha + "\","
            + "\"conclusion\":\"" + conclusion + "\","
            + "\"html_url\":\"https://ci/wf/99\","
            + "\"head_branch\":\"fix/thing\","
            + "\"pull_requests\":[{\"number\":7,"
            + "\"head\":{\"ref\":\"fix/thing\","
            + "\"repo\":{\"full_name\":\"afonsoft/demo\"}}}]}}");

    private static CiCheckEvent CheckEvent(
        string conclusion = "failure", string? appSlug = "actions",
        string? actor = "octocat", string headSha = "head-1") =>
        new("check_run", "afonsoft/demo", headSha, "fix/thing", 7,
            "build", conclusion, "https://ci/1", 42, null,
            appSlug, actor, "afonsoft/demo");

    private async Task<AutofixRunRecord> ReloadAsync(Guid id) =>
        await _db.AutofixRuns.IgnoreQueryFilters()
            .SingleAsync(r => r.Id == id);

    private async Task<Guid> SeedRunAsync(
        string status = AutofixRunStatuses.Queued,
        int attemptCount = 0, string headSha = "head-1")
    {
        var run = new AutofixRunRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            ConnectionId = _connectionId,
            Repository = "afonsoft/demo",
            PrNumber = 7,
            HeadSha = headSha,
            HeadBranch = "fix/thing",
            CheckName = "build",
            CheckKind = "check_run",
            CheckRunId = 42,
            Status = status,
            AttemptCount = attemptCount,
            Trigger = "webhook",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.AutofixRuns.Add(run);
        await _db.SaveChangesAsync();
        return run.Id;
    }

    // ---- RF-001: parser ---------------------------------------------------

    [Fact]
    public void Parser_normalizes_check_run_failure()
    {
        using var doc = JsonDocument.Parse(CheckRunPayload());
        var ev = CiCheckEventParser.Parse("check_run", doc.RootElement);
        ev.ShouldNotBeNull();
        ev.Kind.ShouldBe("check_run");
        ev.Repository.ShouldBe("afonsoft/demo");
        ev.HeadSha.ShouldBe("head-1");
        ev.CheckName.ShouldBe("build");
        ev.Conclusion.ShouldBe("failure");
        ev.CheckRunId.ShouldBe(42);
        ev.PrNumber.ShouldBe(7);
        ev.HeadBranch.ShouldBe("fix/thing");
        ev.HeadRepoFullName.ShouldBe("afonsoft/demo");
    }

    [Fact]
    public void Parser_normalizes_workflow_run_failure()
    {
        using var doc = JsonDocument.Parse(WorkflowRunPayload());
        var ev = CiCheckEventParser.Parse("workflow_run", doc.RootElement);
        ev.ShouldNotBeNull();
        ev.Kind.ShouldBe("workflow_run");
        ev.CheckName.ShouldBe("workflow:CI");
        ev.WorkflowRunId.ShouldBe(99);
        ev.HeadBranch.ShouldBe("fix/thing");
    }

    [Fact]
    public void Parser_normalizes_status_failure()
    {
        using var doc = JsonDocument.Parse("""
            {"repository":{"full_name":"afonsoft/demo"},
             "sha":"head-9","state":"failure","context":"ci/tests",
             "target_url":"https://ci/t",
             "branches":[{"name":"fix/thing"}]}
            """);
        var ev = CiCheckEventParser.Parse("status", doc.RootElement);
        ev.ShouldNotBeNull();
        ev.Kind.ShouldBe("status");
        ev.CheckName.ShouldBe("ci/tests");
        ev.Conclusion.ShouldBe("failure");
        ev.PrNumber.ShouldBeNull();
    }

    [Fact]
    public void Parser_ignores_non_completed_actions_and_unknown_events()
    {
        using var doc = JsonDocument.Parse(
            CheckRunPayload().Select(b => (char)b).Aggregate(
                "", (s, c) => s + c).Replace(
                    "\"completed\"", "\"rerequested\""));
        CiCheckEventParser.Parse("check_run", doc.RootElement)
            .ShouldBeNull();
        CiCheckEventParser.Parse("push", doc.RootElement)
            .ShouldBeNull();
    }

    // ---- RF-001: webhook routing ------------------------------------------

    [Fact]
    public async Task Webhook_check_run_failure_enqueues_autofix()
    {
        _secrets.Value = "hooksecret";
        var payload = CheckRunPayload();
        var result = await Webhook().HandleGitHubAsync(
            Sign(payload), "check_run", "del-141-1", payload,
            CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.Processed);
        _dispatcher.Enqueued.ShouldHaveSingleItem();
        _audit.Actions.ShouldContain("scm.autofix_triggered");
    }

    [Fact]
    public async Task Webhook_workflow_run_failure_enqueues_autofix()
    {
        _secrets.Value = "hooksecret";
        var payload = WorkflowRunPayload();
        var result = await Webhook().HandleGitHubAsync(
            Sign(payload), "workflow_run", "del-141-2", payload,
            CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.Processed);
        _dispatcher.Enqueued.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Webhook_check_run_success_is_ignored()
    {
        _secrets.Value = "hooksecret";
        var payload = CheckRunPayload(conclusion: "success");
        var result = await Webhook().HandleGitHubAsync(
            Sign(payload), "check_run", "del-141-3", payload,
            CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.Ignored);
        _dispatcher.Enqueued.ShouldBeEmpty();
    }

    // ---- RF-002: policy ---------------------------------------------------

    [Fact]
    public async Task Trigger_creates_queued_run_and_dispatches()
    {
        var result = await Service().TriggerCiEventAsync(
            Connection(), CheckEvent(), CancellationToken.None);
        result.RunId.ShouldNotBeNull();
        _dispatcher.Enqueued.ShouldHaveSingleItem()
            .ShouldBe(result.RunId!.Value);
        var run = await ReloadAsync(result.RunId!.Value);
        run.Status.ShouldBe(AutofixRunStatuses.Queued);
        run.CheckKind.ShouldBe("check_run");
        run.Trigger.ShouldBe("webhook");
        run.HeadBranch.ShouldBe("fix/thing");
    }

    [Fact]
    public async Task Trigger_dedups_same_head_and_check()
    {
        var svc = Service();
        (await svc.TriggerCiEventAsync(Connection(), CheckEvent(),
            CancellationToken.None)).RunId.ShouldNotBeNull();
        var dup = await svc.TriggerCiEventAsync(
            Connection(), CheckEvent(), CancellationToken.None);
        dup.Error.ShouldBe(AutofixTriggerError.Duplicate);
        _dispatcher.Enqueued.ShouldHaveSingleItem();

        // head novo → nova cadeia (synchronize).
        var next = await svc.TriggerCiEventAsync(
            Connection(), CheckEvent(headSha: "head-2"),
            CancellationToken.None);
        // ainda bloqueia — o primeiro run continua ativo no PR.
        next.Error.ShouldBe(AutofixTriggerError.Duplicate);
    }

    [Fact]
    public async Task Trigger_new_head_after_terminal_run_creates_new_run()
    {
        var svc = Service();
        var first = await svc.TriggerCiEventAsync(
            Connection(), CheckEvent(), CancellationToken.None);
        (await ReloadAsync(first.RunId!.Value)).Status =
            AutofixRunStatuses.Completed;
        await _db.SaveChangesAsync();

        var next = await svc.TriggerCiEventAsync(
            Connection(), CheckEvent(headSha: "head-2"),
            CancellationToken.None);
        next.RunId.ShouldNotBeNull();
        _dispatcher.Enqueued.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Trigger_skips_when_autofix_disabled()
    {
        var result = await Service().TriggerCiEventAsync(
            Connection(autofixEnabled: false), CheckEvent(),
            CancellationToken.None);
        result.Error.ShouldBe(AutofixTriggerError.Disabled);
        _dispatcher.Enqueued.ShouldBeEmpty();
    }

    [Fact]
    public async Task Trigger_skips_own_checks_and_bot_actors()
    {
        var svc = Service();
        // contexto sandcastle/* (commit status próprio).
        (await svc.TriggerCiEventAsync(Connection(),
            CheckEvent() with { CheckName = "sandcastle/autofix" },
            CancellationToken.None))
            .Error.ShouldBe(AutofixTriggerError.Ignored);
        // check publicado pelo próprio GitHub App.
        (await svc.TriggerCiEventAsync(Connection(),
            CheckEvent(appSlug: "sandcastle-agent"),
            CancellationToken.None))
            .Error.ShouldBe(AutofixTriggerError.Ignored);
        // ator bot configurado.
        (await svc.TriggerCiEventAsync(Connection(),
            CheckEvent(actor: "github-actions[bot]"),
            CancellationToken.None))
            .Error.ShouldBe(AutofixTriggerError.Ignored);
        // ator = login do nosso app bot.
        (await svc.TriggerCiEventAsync(Connection(),
            CheckEvent(actor: "sandcastle-agent[bot]"),
            CancellationToken.None))
            .Error.ShouldBe(AutofixTriggerError.Ignored);
        _dispatcher.Enqueued.ShouldBeEmpty();
    }

    [Fact]
    public async Task Trigger_skips_head_pushed_by_own_fix()
    {
        // anti-loop: head_sha_out de uma tentativa anterior → o check
        // desse commit é observado pelo poll interno, não por run novo.
        var runId = await SeedRunAsync(
            status: AutofixRunStatuses.Completed);
        _db.AutofixAttempts.Add(new AutofixAttemptRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            RunId = runId,
            Number = 1,
            HeadShaIn = "head-0",
            HeadShaOut = "head-1",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();

        var result = await Service().TriggerCiEventAsync(
            Connection(), CheckEvent(), CancellationToken.None);
        result.Error.ShouldBe(AutofixTriggerError.Ignored);
        result.Detail!.ShouldContain("pushed by autofix");
    }

    [Fact]
    public async Task Trigger_respects_check_name_glob()
    {
        var svc = Service(new AutofixOptions
        {
            CheckNamePatterns = ["*lint*", "*test*"],
        });
        (await svc.TriggerCiEventAsync(Connection(),
            CheckEvent() with { CheckName = "deploy" },
            CancellationToken.None))
            .Error.ShouldBe(AutofixTriggerError.Ignored);
        (await svc.TriggerCiEventAsync(Connection(),
            CheckEvent() with { CheckName = "lint" },
            CancellationToken.None))
            .RunId.ShouldNotBeNull();
    }

    [Fact]
    public async Task Trigger_skips_fork_pr_head()
    {
        var result = await Service().TriggerCiEventAsync(
            Connection(),
            CheckEvent() with { HeadRepoFullName = "someone/demo" },
            CancellationToken.None);
        result.Error.ShouldBe(AutofixTriggerError.Ignored);
        result.Detail!.ShouldContain("fork");
    }

    [Fact]
    public async Task Trigger_resolves_pr_via_commit_pulls_lookup()
    {
        _provider.CommitPulls =
            [new ScmCommitPullRequest(9, "open", "head-1",
                "fix/thing", "afonsoft/demo")];
        var ev = CheckEvent() with { PrNumber = null, HeadBranch = null };
        var result = await Service().TriggerCiEventAsync(
            Connection(), ev, CancellationToken.None);
        result.RunId.ShouldNotBeNull();
        (await ReloadAsync(result.RunId!.Value)).PrNumber.ShouldBe(9);
    }

    [Fact]
    public async Task Trigger_no_pr_is_ignored()
    {
        var ev = CheckEvent() with { PrNumber = null };
        var result = await Service().TriggerCiEventAsync(
            Connection(), ev, CancellationToken.None);
        result.Error.ShouldBe(AutofixTriggerError.Ignored);
    }

    [Fact]
    public async Task Manual_trigger_and_cancel_work()
    {
        var svc = Service();
        var result = await svc.TriggerManualAsync(
            Connection(), "afonsoft/demo", 7, "user-1",
            CancellationToken.None);
        result.RunId.ShouldNotBeNull();
        var run = await ReloadAsync(result.RunId!.Value);
        run.Trigger.ShouldBe("manual");
        run.RequestedByUserId.ShouldBe("user-1");

        // cancel transiciona run ativo → cancelled.
        (await svc.CancelAsync(_orgId, result.RunId!.Value,
            CancellationToken.None)).ShouldBeTrue();
        (await ReloadAsync(result.RunId!.Value)).Status
            .ShouldBe(AutofixRunStatuses.Cancelled);
        // segundo cancel → false (já terminal).
        (await svc.CancelAsync(_orgId, result.RunId!.Value,
            CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Manual_trigger_rejects_disabled_and_unknown_pr()
    {
        var svc = Service();
        (await svc.TriggerManualAsync(
            Connection(autofixEnabled: false), "afonsoft/demo", 7,
            null, CancellationToken.None))
            .Error.ShouldBe(AutofixTriggerError.Disabled);
        _provider.PullRequest = null;
        (await svc.TriggerManualAsync(
            Connection(), "afonsoft/demo", 99, null,
            CancellationToken.None))
            .Error.ShouldBe(AutofixTriggerError.NotFound);
    }

    [Fact]
    public async Task Search_filters_by_repository_pr_status()
    {
        var svc = Service();
        var a = await svc.TriggerCiEventAsync(
            Connection(), CheckEvent(), CancellationToken.None);
        (await ReloadAsync(a.RunId!.Value)).Status =
            AutofixRunStatuses.Completed;
        await _db.SaveChangesAsync();
        await svc.TriggerCiEventAsync(
            Connection(), CheckEvent(headSha: "head-2"),
            CancellationToken.None);

        var page = await svc.SearchAsync(_orgId, "afonsoft/demo", 7,
            AutofixRunStatuses.Completed, null, 50,
            CancellationToken.None);
        page.Items.ShouldHaveSingleItem().Id.ShouldBe(a.RunId!.Value);

        var detail = await svc.GetAsync(
            _orgId, a.RunId!.Value, CancellationToken.None);
        detail.ShouldNotBeNull();
        detail.Run.CheckName.ShouldBe("build");
    }

    // ---- RF-002: classifier ------------------------------------------------

    [Theory]
    [InlineData("lint", "eslint error: unexpected console", CiFailureClass.Lint)]
    [InlineData("typecheck", "src/a.ts(1,2): error TS2322", CiFailureClass.Type)]
    [InlineData("test", "  ✕ renders page — Expected: 3, Actual: 2", CiFailureClass.Test)]
    [InlineData("build", "error MSB1009: project file does not exist", CiFailureClass.Build)]
    [InlineData("build", "docker: no space left on device", CiFailureClass.Infra)]
    [InlineData("build", "connection refused pulling image", CiFailureClass.Infra)]
    [InlineData("build", "job timed out after 360 minutes", CiFailureClass.Timeout)]
    [InlineData("build", "the self-hosted runner: agent lost communication", CiFailureClass.Infra)]
    [InlineData("build", "error: failed to download package foo", CiFailureClass.Infra)]
    [InlineData("build", "npm err! econnreset network flaky", CiFailureClass.Flaky)]
    [InlineData("build", "Resource not accessible by integration", CiFailureClass.Permission)]
    [InlineData("build", "environment variable NPM_TOKEN is not set", CiFailureClass.Secrets)]
    [InlineData("deploy", "random gibberish with no known markers", CiFailureClass.Unknown)]
    public void Classifier_detects_each_class(
        string checkName, string log, CiFailureClass expected) =>
        CiFailureClassifier.Classify(checkName, log, null)
            .ShouldBe(expected);

    [Fact]
    public void Classifier_honors_configured_flaky_patterns()
    {
        CiFailureClassifier.Classify("test",
            "Error: Snapshot obsolete but written to disk",
            ["snapshot obsolete"]).ShouldBe(CiFailureClass.Flaky);
        CiFailureClassifier.IsFixable(CiFailureClass.Flaky).ShouldBeFalse();
        CiFailureClassifier.IsFixable(CiFailureClass.Lint).ShouldBeTrue();
    }

    // ---- RF-003: runner (worker flow) -------------------------------------

    [Fact]
    public async Task Runner_green_path_completes_and_reports()
    {
        var runId = await SeedRunAsync();
        // após o push do agente: PR head muda + checks terminais verdes.
        _provider.PullRequest = new ScmPullRequest(
            "https://gh/pr/7", 7, "open", "head-fixed-1", "pr");
        _provider.CheckRuns =
        [
            new ScmCheckRun(1, "build", "completed", "success",
                "head-fixed-1", null),
        ];

        await Runner().RunAsync(runId, CancellationToken.None);

        var run = await ReloadAsync(runId);
        run.Status.ShouldBe(AutofixRunStatuses.Completed);
        run.AttemptCount.ShouldBe(1);
        run.ConversationId.ShouldNotBeNull();

        var attempt = await _db.AutofixAttempts.IgnoreQueryFilters()
            .SingleAsync(a => a.RunId == runId);
        attempt.Outcome.ShouldBe(AutofixAttemptOutcomes.Fixed);
        attempt.HeadShaOut.ShouldBe("head-fixed-1");

        // conversa de fix criada com agent_type=autofix + repo.
        var conv = _conversations.Calls.ShouldHaveSingleItem();
        conv.Request.AgentType.ShouldBe(AutofixDefaults.AgentType);
        conv.Request.SelectedRepository.ShouldBe("afonsoft/demo");
        conv.Request.InitialMessage!.Content[0].ToString()!
            .ShouldContain("fix/thing");

        // status advisory success + comentário resumo + audit.
        _provider.Statuses.ShouldContain(s =>
            s.Context == AutofixDefaults.StatusContext
            && s.State == "success");
        _provider.IssueComments.ShouldContain(c =>
            c.Body.Contains("green after 1 attempt"));
        _audit.Actions.ShouldContain("scm.autofix_completed");
    }

    [Fact]
    public async Task Runner_red_path_consumes_attempt_and_iterates()
    {
        var runId = await SeedRunAsync();
        // push do fix → check ainda vermelho → próxima tentativa.
        _provider.PullRequest = new ScmPullRequest(
            "https://gh/pr/7", 7, "open", "head-fixed-1", "pr");
        _provider.CheckRuns =
        [
            new ScmCheckRun(5, "lint", "completed", "failure",
                "head-fixed-1", null),
        ];

        await Runner(new AutofixOptions
        {
            PollSeconds = 1,
            RunTimeoutMinutes = 1,
            MaxAttempts = 1, // cap=1 → give up na checagem seguinte
        }).RunAsync(runId, CancellationToken.None);

        var run = await ReloadAsync(runId);
        run.Status.ShouldBe(AutofixRunStatuses.Failed);
        run.AttemptCount.ShouldBe(1);
        run.Error!.ShouldContain("attempt cap");
        var attempt = await _db.AutofixAttempts.IgnoreQueryFilters()
            .SingleAsync(a => a.RunId == runId);
        attempt.Outcome.ShouldBe(AutofixAttemptOutcomes.StillFailing);
        _provider.Statuses.ShouldContain(s =>
            s.State == "failure");
        _provider.IssueComments.ShouldContain(c =>
            c.Body.Contains("giving up"));
        _audit.Actions.ShouldContain("scm.autofix_failed");
    }

    [Fact]
    public async Task Runner_infra_failure_blocks_without_consuming_attempt()
    {
        var runId = await SeedRunAsync();
        _provider.CheckRunLog =
            "docker: cannot connect to the docker daemon";

        await Runner().RunAsync(runId, CancellationToken.None);

        var run = await ReloadAsync(runId);
        run.Status.ShouldBe(AutofixRunStatuses.Blocked);
        run.AttemptCount.ShouldBe(0); // bail-out não consome tentativa
        _conversations.Calls.ShouldBeEmpty();
        _provider.IssueComments.ShouldContain(c =>
            c.Body.Contains("skipping"));
        _audit.Actions.ShouldContain("scm.autofix_failed");
    }

    [Fact]
    public async Task Runner_at_cap_gives_up_immediately()
    {
        var runId = await SeedRunAsync(
            status: AutofixRunStatuses.Running, attemptCount: 3);
        await Runner().RunAsync(runId, CancellationToken.None);

        var run = await ReloadAsync(runId);
        run.Status.ShouldBe(AutofixRunStatuses.Failed);
        _conversations.Calls.ShouldBeEmpty();
        _provider.Statuses.ShouldContain(s =>
            s.State == "failure" && s.Description!.Contains("3"));
    }

    [Fact]
    public async Task Runner_cancelled_run_exits()
    {
        var runId = await SeedRunAsync(
            status: AutofixRunStatuses.Cancelled);
        await Runner().RunAsync(runId, CancellationToken.None);
        (await ReloadAsync(runId)).Status
            .ShouldBe(AutofixRunStatuses.Cancelled);
        _conversations.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Runner_pr_closed_fails_run()
    {
        var runId = await SeedRunAsync();
        _provider.PullRequest = new ScmPullRequest(
            "https://gh/pr/7", 7, "closed", "head-1", "pr");

        await Runner().RunAsync(runId, CancellationToken.None);

        var run = await ReloadAsync(runId);
        run.Status.ShouldBe(AutofixRunStatuses.Failed);
        run.Error!.ShouldContain("closed");
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    // ---- fakes ------------------------------------------------------------

    private sealed class FakeProvider : IScmProvider
    {
        public ScmPullRequest? PullRequest = new(
            "https://gh/pr/7", 7, "open", "head-1", "demo pr");
        public IReadOnlyList<ScmCheckRun> CheckRuns = [];
        public IReadOnlyList<ScmCommitPullRequest> CommitPulls = [];
        public string? CheckRunLog = "npm run build\nerror MSB1009";
        public List<(int Number, string Body)> IssueComments = [];
        public List<(string Sha, string State, string Context,
            string? Description)> Statuses = [];

        public ScmKind Kind => ScmKind.GitHub;

        public Task<ScmPullRequest?> GetPullRequestAsync(
            ScmConnection c, string r, int n, CancellationToken ct) =>
            Task.FromResult(PullRequest);

        public Task<IReadOnlyList<ScmCheckRun>> GetCheckRunsAsync(
            ScmConnection c, string r, string rf,
            CancellationToken ct) => Task.FromResult(CheckRuns);

        public Task<string?> GetCheckRunLogsAsync(
            ScmConnection c, string r, long i,
            CancellationToken ct) => Task.FromResult(CheckRunLog);

        public Task<string?> GetWorkflowRunLogsAsync(
            ScmConnection c, string r, long i,
            CancellationToken ct) => Task.FromResult(CheckRunLog);

        public Task<IReadOnlyList<ScmCommitPullRequest>>
            GetPullRequestsForCommitAsync(
                ScmConnection c, string r, string s,
                CancellationToken ct) => Task.FromResult(CommitPulls);

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

        public Task<ScmPullRequest> CreatePullRequestAsync(
            ScmConnection c, string r, ScmPrCreateRequest q,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<long> ReplyToReviewCommentAsync(ScmConnection c,
            string r, int p, long i, string b, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<ScmConnectionDiagnostic> TestConnectionAsync(
            ScmConnection c, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<ScmPullRequestFile>>
            GetPullRequestFilesAsync(ScmConnection c, string r, int n,
                CancellationToken ct) =>
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
            IReadOnlyList<ScmReviewComment> cs, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class FakeRegistry(IScmProvider provider)
        : IScmProviderRegistry
    {
        public IScmProvider? Get(ScmKind kind) =>
            kind == ScmKind.GitHub ? provider : null;
    }

    private sealed class FakeAutofixDispatcher : IAutofixDispatcher
    {
        public List<Guid> Enqueued = [];

        public ValueTask EnqueueAsync(Guid autofixRunId,
            CancellationToken ct)
        {
            Enqueued.Add(autofixRunId);
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

    private sealed class CapturingConversations : IAppConversationService
    {
        public List<(Guid UserId, Guid? OrgId, CreateConversationRequest
            Request)> Calls = [];

        public Task<AppConversationStartTask> CreateForUserAsync(
            Guid userId, Guid? orgId, CreateConversationRequest request,
            CancellationToken ct)
        {
            Calls.Add((userId, orgId, request));
            return Task.FromResult(new AppConversationStartTask(
                Guid.NewGuid(), StartTaskStatus.Working,
                Guid.NewGuid(), null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        public Task<AppConversationStartTask> CreateAsync(
            CreateConversationRequest request, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<ConversationPage> SearchAsync(
            ConversationQuery query, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<long> CountAsync(ConversationQuery query,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<IReadOnlyList<AppConversation>> GetBatchAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<AppConversation?> GetAsync(Guid id,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<AppConversation?> UpdateAsync(Guid id, string? title,
            IReadOnlyList<string>? tags, bool? pinned,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<SendMessageError?> ValidateSendMessageAsync(Guid id,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<SwitchResult> SwitchProfileAsync(Guid id,
            string? profile, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<SwitchResult> SwitchModelAsync(Guid id, string model,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<AppConversation?> SetPermissionsAsync(Guid id,
            IReadOnlyList<Application.Agent.AgentPermissionRule>? rules,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<AppConversation?> SetModeAsync(Guid id, string mode,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<IReadOnlyList<AttentionItem>> ListAttentionAsync(
            int limit, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<AttentionDismissResult> DismissAttentionAsync(
            Guid id, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class FakeSandboxes : ISandboxService
    {
        public Task<SandboxModel?> GetAsync(Guid sandboxId,
            CancellationToken ct) =>
            Task.FromResult<SandboxModel?>(new SandboxModel(
                sandboxId, Guid.NewGuid(), null, null,
                SandboxStatus.Running, null,
                DateTimeOffset.UtcNow, null, null));

        public Task<SandboxModel> StartAsync(Guid specId,
            Guid? conversationId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<SandboxModel> PauseAsync(Guid sandboxId,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<SandboxModel> StopAsync(Guid sandboxId,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<SandboxModel> ResumeAsync(Guid sandboxId,
            CancellationToken ct) => throw new NotImplementedException();

        public Task DeleteAsync(Guid sandboxId, CancellationToken ct) =>
            Task.CompletedTask;

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

    private sealed class FakeGit : IGitService
    {
        public bool TokenProvisioned;

        public Task<bool> ProvisionTokenAuthAsync(SandboxModel sandbox,
            string host, string token, CancellationToken ct)
        {
            TokenProvisioned = true;
            return Task.FromResult(true);
        }

        public Task<bool> ProvisionGhAuthAsync(SandboxModel sandbox,
            string? ownerId, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<GitPushResult> PushAsync(SandboxModel sandbox,
            string head, string? remoteBranch, bool forceWithLease,
            string? ownerId, CancellationToken ct,
            string? repoPath = null) =>
            throw new NotImplementedException();

        public Task<string?> GetCurrentBranchAsync(SandboxModel sandbox,
            CancellationToken ct, string? repoPath = null) =>
            throw new NotImplementedException();

        public Task<bool> HasRepositoryAsync(SandboxModel sandbox,
            CancellationToken ct, string? repoPath = null) =>
            Task.FromResult(true);

        public Task CloneAsync(SandboxModel sandbox, string url,
            string branch, string? owner, CancellationToken ct,
            string? repoPath = null, string? credentialToken = null) =>
            Task.CompletedTask;

        public Task<GitChanges> GetChangesAsync(SandboxModel sandbox,
            CancellationToken ct, string? repoPath = null) =>
            throw new NotImplementedException();

        public Task<GitDiffReport?> GetDiffAsync(SandboxModel sandbox,
            string? path, CancellationToken ct, string? repoPath = null) =>
            throw new NotImplementedException();

        public Task<string?> GetRawDiffAsync(SandboxModel sandbox,
            string? path, CancellationToken ct, string? repoPath = null) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<GitLogEntry>> GetLogAsync(
            SandboxModel sandbox, int limit, CancellationToken ct,
            string? repoPath = null) =>
            throw new NotImplementedException();

        public Task<string?> GetFileAtRefAsync(SandboxModel sandbox,
            string path, string reference, CancellationToken ct,
            string? repoPath = null) =>
            throw new NotImplementedException();

        public Task<GitCommitResult> CommitAsync(SandboxModel sandbox,
            string message, IReadOnlyList<string>? files,
            CancellationToken ct, string? repoPath = null) =>
            throw new NotImplementedException();
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

    private sealed class FakeSecrets
        : Sandcastle.Application.Contracts.ISecretStore
    {
        public string Value = "hooksecret";

        public Task<string?> ResolveAsync(string scope, string ownerId,
            string name, CancellationToken ct) =>
            Task.FromResult<string?>(Value);

        public Task<IReadOnlyList<string>> ListNamesAsync(string scope,
            string ownerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task SetAsync(string scope, string ownerId, string name,
            string plaintext, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> DeleteAsync(string scope, string ownerId,
            string name, CancellationToken ct) => Task.FromResult(true);
    }
}