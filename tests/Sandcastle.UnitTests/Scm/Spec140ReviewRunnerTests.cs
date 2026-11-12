using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Coordination;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Scm;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Scm;
using Sandcastle.Infrastructure.Scm.Review;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Scm;

/// <summary>
/// SPEC-140 RF-002/RF-003/RF-005 — <see cref="ReviewRunner"/> de ponta a
/// ponta com provider+LLM fake: posting (inline/summary/status), dedup
/// por diff hash, soft-fail em output inválido, fallback de provider sem
/// review API, pass-condition e <see cref="ReviewService"/> (dedup por
/// head + quota diária).
/// </summary>
public sealed class Spec140ReviewRunnerTests : IAsyncDisposable
{
    private const string Patch = """
        @@ -1,3 +1,5 @@ class Foo {
         line1
        -old
        +newA
        +newB
         line2
        """;

    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly Guid _orgId = Guid.NewGuid();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly FakeProvider _provider = new();
    private readonly FakeUsage _usage = new();
    private readonly CapturingAudit _audit = new();
    private readonly FakeDispatcher _dispatcher = new();

    public Spec140ReviewRunnerTests()
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
            ReviewEnabled = true,
            CreatedByUserId = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
    }

    private static string LlmScript(string findingsJson) =>
        $$"""{"text":{{System.Text.Json.JsonSerializer.Serialize(findingsJson)}}}""";

    private ReviewRunner Runner(string findingsJson,
        ReviewOptions? options = null) =>
        new(_db, new FakeRegistry(_provider),
            new MockLlmProvider(
                [JsonNode.Parse(LlmScript(findingsJson))!]),
            new FakeRouter(),
            Options.Create(options ?? new ReviewOptions()),
            _usage, _audit,
            NullLogger<ReviewRunner>.Instance);

    private ReviewService Service(ReviewOptions? options = null) =>
        new(_db, _dispatcher,
            Options.Create(options ?? new ReviewOptions()),
            NullLogger<ReviewService>.Instance);

    private ScmConnection Connection() =>
        ScmConnectionService.ToModel(_db.ScmConnections
            .IgnoreQueryFilters().Single(c => c.Id == _connectionId));

    private async Task<Guid> SeedRunAsync(string headSha = "head-1",
        string status = ReviewRunStatuses.Queued,
        string? diffHash = null)
    {
        var run = new ReviewRunRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            ConnectionId = _connectionId,
            Repository = "afonsoft/demo",
            PrNumber = 7,
            HeadSha = headSha,
            DiffHash = diffHash,
            Status = status,
            Trigger = "webhook",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.ReviewRuns.Add(run);
        await _db.SaveChangesAsync();
        return run.Id;
    }

    private Task<ReviewRunRecord> ReloadAsync(Guid id) =>
        _db.ReviewRuns.IgnoreQueryFilters().SingleAsync(r => r.Id == id);

    // ---- RF-002/RF-003: run completo --------------------------------------

    [Fact]
    public async Task Run_posts_inline_review_status_and_persists_findings()
    {
        var runId = await SeedRunAsync();
        var findings = """
            {"findings":[{"path":"src/Foo.cs","line":3,
              "severity":"critical","kind":"bug","title":"Null deref",
              "body":"x can be null here","confidence":0.9,
              "suggested_patch":"if (x is null) return;"}]}
            """;

        await Runner(findings).RunAsync(runId, CancellationToken.None);

        var run = await ReloadAsync(runId);
        run.Status.ShouldBe(ReviewRunStatuses.Completed);
        run.FinishedAt.ShouldNotBeNull();
        run.StatsJson!.ShouldContain("\"findings\":1");

        // review ancorada: summary + 1 comentário inline na linha nova.
        var review = _provider.Reviews.ShouldHaveSingleItem();
        review.HeadSha.ShouldBe("head-1");
        var comment = review.Comments.ShouldHaveSingleItem();
        comment.Path.ShouldBe("src/Foo.cs");
        comment.Line.ShouldBe(3);
        comment.Body.ShouldContain("🔴");
        comment.Body.ShouldContain("```suggestion");
        review.Body.ShouldContain("1 finding");

        // commit status advisory — default completed ⇒ sempre success.
        var status = _provider.Statuses.ShouldHaveSingleItem();
        status.Context.ShouldBe("sandcastle/review");
        status.State.ShouldBe("success");
        status.Description!.ShouldContain("advisory");

        // finding persistido + usage metering + audit.
        var finding = await _db.ReviewFindings.IgnoreQueryFilters()
            .SingleAsync(f => f.ReviewRunId == runId);
        finding.Severity.ShouldBe(ReviewSeverities.Critical);
        finding.Status.ShouldBe(ReviewFindingStatuses.Open);
        _usage.Tokens.ShouldBeGreaterThan(0);
        _audit.Actions.ShouldContain("scm.review_completed");
    }

    [Fact]
    public async Task Run_with_same_diff_is_skipped()
    {
        await Runner("""{"findings":[]}""")
            .RunAsync(await SeedRunAsync("head-1"), CancellationToken.None);

        // head novo (synchronize) mas diff idêntico → re-review skip.
        var twinId = await SeedRunAsync("head-2");
        await Runner("""{"findings":[]}""")
            .RunAsync(twinId, CancellationToken.None);

        var twin = await ReloadAsync(twinId);
        twin.Status.ShouldBe(ReviewRunStatuses.Skipped);
        _provider.Reviews.Count.ShouldBe(1); // só o primeiro run postou
    }

    [Fact]
    public async Task Run_with_new_diff_re_reviews()
    {
        await Runner("""{"findings":[]}""")
            .RunAsync(await SeedRunAsync("head-1"), CancellationToken.None);

        _provider.Files = [
            new ScmPullRequestFile("src/Other.cs", null, "modified",
                2, 1, Patch)];
        var twinId = await SeedRunAsync("head-2");
        await Runner("""{"findings":[]}""")
            .RunAsync(twinId, CancellationToken.None);

        (await ReloadAsync(twinId)).Status
            .ShouldBe(ReviewRunStatuses.Completed);
        _provider.Reviews.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Invalid_llm_output_soft_fails_as_zero_findings()
    {
        var runId = await SeedRunAsync();
        await Runner("not json at all — hallucinated prose")
            .RunAsync(runId, CancellationToken.None);

        var run = await ReloadAsync(runId);
        run.Status.ShouldBe(ReviewRunStatuses.Completed);
        _provider.Reviews.ShouldHaveSingleItem().Body
            .ShouldContain("No issues found");
        (await _db.ReviewFindings.IgnoreQueryFilters()
            .CountAsync(f => f.ReviewRunId == runId)).ShouldBe(0);
    }

    [Fact]
    public async Task Provider_without_review_api_falls_back_to_comment()
    {
        _provider.ReviewResult = null; // provider sem CreateReview
        var runId = await SeedRunAsync();
        var findings = """
            {"findings":[{"path":"src/Foo.cs","line":3,
              "severity":"important","kind":"security",
              "body":"api key hardcoded"}]}
            """;

        await Runner(findings).RunAsync(runId, CancellationToken.None);

        _provider.Reviews.ShouldBeEmpty();
        var comment = _provider.IssueComments.ShouldHaveSingleItem();
        comment.Body.ShouldContain("api key hardcoded");
        (await ReloadAsync(runId)).Status
            .ShouldBe(ReviewRunStatuses.Completed);
    }

    [Fact]
    public async Task No_critical_pass_condition_flips_status_to_failure()
    {
        var rec = await _db.ScmConnections.IgnoreQueryFilters()
            .SingleAsync(c => c.Id == _connectionId);
        rec.ReviewPassCondition = ReviewDefaults.PassConditionNoCritical;
        await _db.SaveChangesAsync();

        var runId = await SeedRunAsync();
        var findings = """
            {"findings":[{"path":"src/Foo.cs","line":3,
              "severity":"critical","kind":"bug","body":"boom"}]}
            """;

        await Runner(findings).RunAsync(runId, CancellationToken.None);

        _provider.Statuses.ShouldHaveSingleItem().State
            .ShouldBe("failure");
    }

    [Fact]
    public async Task Provider_error_marks_run_failed_and_posts_error_status()
    {
        _provider.FilesError = new HttpRequestException("github down");
        var runId = await SeedRunAsync();

        await Runner("""{"findings":[]}""")
            .RunAsync(runId, CancellationToken.None);

        var run = await ReloadAsync(runId);
        run.Status.ShouldBe(ReviewRunStatuses.Failed);
        run.Error!.ShouldContain("github down");
        _provider.Statuses.ShouldHaveSingleItem().State.ShouldBe("error");
        _audit.Actions.ShouldContain("scm.review_failed");
    }

    [Fact]
    public async Task Prior_open_findings_of_same_pr_become_stale()
    {
        var firstId = await SeedRunAsync("head-1");
        var findings = """
            {"findings":[{"path":"src/Foo.cs","line":3,
              "severity":"nit","kind":"style","body":"naming"}]}
            """;
        await Runner(findings).RunAsync(firstId, CancellationToken.None);

        _provider.Files = [
            new ScmPullRequestFile("src/Foo.cs", null, "modified",
                3, 1, Patch + "\n+extra")];
        var secondId = await SeedRunAsync("head-2");
        await Runner("""{"findings":[]}""")
            .RunAsync(secondId, CancellationToken.None);

        var old = await _db.ReviewFindings.IgnoreQueryFilters()
            .SingleAsync(f => f.ReviewRunId == firstId);
        old.Status.ShouldBe(ReviewFindingStatuses.Stale);
    }

    // ---- RF-001/RF-005: ReviewService (dedup + quota) ---------------------

    [Fact]
    public async Task Trigger_creates_run_and_enqueues()
    {
        var result = await Service().TriggerAsync(
            Connection(), "afonsoft/demo", 7, "head-1",
            "manual", "user-1", CancellationToken.None);

        result.ReviewRunId.ShouldNotBeNull();
        _dispatcher.Enqueued.ShouldHaveSingleItem()
            .ShouldBe(result.ReviewRunId!.Value);
        (await ReloadAsync(result.ReviewRunId!.Value)).Status
            .ShouldBe(ReviewRunStatuses.Queued);
    }

    [Fact]
    public async Task Trigger_dedups_same_head()
    {
        var svc = Service();
        var first = await svc.TriggerAsync(Connection(), "afonsoft/demo",
            7, "head-1", "webhook", null, CancellationToken.None);
        first.ReviewRunId.ShouldNotBeNull();

        var dup = await svc.TriggerAsync(Connection(), "afonsoft/demo",
            7, "head-1", "webhook", null, CancellationToken.None);
        dup.Error.ShouldBe(ReviewTriggerError.Duplicate);
        dup.ReviewRunId.ShouldBeNull();
        _dispatcher.Enqueued.ShouldHaveSingleItem();

        // head diferente → novo run (synchronize).
        var next = await svc.TriggerAsync(Connection(), "afonsoft/demo",
            7, "head-2", "webhook", null, CancellationToken.None);
        next.ReviewRunId.ShouldNotBeNull();
    }

    [Fact]
    public async Task Trigger_enforces_daily_quota()
    {
        var svc = Service(new ReviewOptions { MaxDailyRunsPerRepo = 2 });
        for (var i = 0; i < 2; i++)
        {
            (await svc.TriggerAsync(Connection(), "afonsoft/demo", 7,
                $"head-{i}", "webhook", null, CancellationToken.None))
                .ReviewRunId.ShouldNotBeNull();
        }
        var blocked = await svc.TriggerAsync(Connection(), "afonsoft/demo",
            7, "head-3", "webhook", null, CancellationToken.None);
        blocked.Error.ShouldBe(ReviewTriggerError.QuotaExceeded);

        // outro repo tem quota própria.
        (await svc.TriggerAsync(Connection(), "afonsoft/other", 7,
            "head-3", "webhook", null, CancellationToken.None))
            .ReviewRunId.ShouldNotBeNull();
    }

    [Fact]
    public async Task Search_filters_and_detail_loads_findings()
    {
        var svc = Service();
        var trig = await svc.TriggerAsync(Connection(), "afonsoft/demo",
            7, "head-1", "webhook", null, CancellationToken.None);
        var runId = trig.ReviewRunId!.Value;
        _db.ReviewFindings.Add(new ReviewFindingRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            ReviewRunId = runId,
            Repository = "afonsoft/demo",
            PrNumber = 7,
            Kind = ReviewFindingKinds.Bug,
            Severity = ReviewSeverities.Important,
            Path = "src/Foo.cs",
            Line = 3,
            Body = "b",
            Status = ReviewFindingStatuses.Open,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();

        var page = await svc.SearchAsync(_orgId, "afonsoft/demo", 7,
            null, null, 50, CancellationToken.None);
        var item = page.Items.ShouldHaveSingleItem();
        item.FindingCount.ShouldBe(1);

        var detail = await svc.GetAsync(_orgId, runId,
            CancellationToken.None);
        detail!.Findings.ShouldHaveSingleItem().Path
            .ShouldBe("src/Foo.cs");

        // PATCH open → dismissed persiste.
        var fid = detail.Findings[0].Id;
        var patched = await svc.UpdateFindingStatusAsync(
            _orgId, runId, fid, ReviewFindingStatuses.Dismissed,
            CancellationToken.None);
        patched!.Status.ShouldBe(ReviewFindingStatuses.Dismissed);
        (await svc.UpdateFindingStatusAsync(_orgId, runId, fid,
            "bogus", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Search_paginates_guid_boundary_without_duplicates()
    {
        // #424: SQLite persiste Guid TEXT uppercase e o cursor carrega
        // lowercase — com CreatedAt idêntico toda quebra de página cai no
        // tie-break por id; sem normalização a linha do cursor repete.
        var at = DateTimeOffset.UtcNow;
        var seeded = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var run = new ReviewRunRecord
            {
                Id = Guid.NewGuid(),
                OrgId = _orgId,
                ConnectionId = _connectionId,
                Repository = "afonsoft/demo",
                PrNumber = 7,
                HeadSha = $"head-{i}",
                Status = ReviewRunStatuses.Completed,
                Trigger = "webhook",
                CreatedAt = at,
            };
            _db.ReviewRuns.Add(run);
            seeded.Add(run.Id);
        }
        await _db.SaveChangesAsync();

        var svc = Service();
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await svc.SearchAsync(_orgId, null, null, null,
                cursor, 2, CancellationToken.None);
            seen.AddRange(page.Items.Select(r => r.Id));
            cursor = page.NextPageId;
        }
        while (cursor is not null);

        seen.ShouldBe(seeded, ignoreOrder: true);
    }

    // ---- worker: reconcile de órfãos no boot ------------------------------

    [Fact]
    public async Task Worker_requeues_orphan_queued_run_on_start()
    {
        // Hygiene (#424-followup): a fila é in-process — run `queued` de
        // um processo anterior volta ao canal no boot do worker.
        var runId = await SeedRunAsync();
        var runner = new RecordingRunner();
        await using var sp = WorkerProvider(runner);
        var worker = new ReviewRunWorker(new ReviewDispatcher(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            new NullDistributedLock(),
            NullLogger<ReviewRunWorker>.Instance);

        using var cts = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));
        await worker.StartAsync(cts.Token);
        try
        {
            (await runner.Next.WaitAsync(cts.Token)).ShouldBe(runId);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_fails_interrupted_running_run_as_worker_restart()
    {
        // `running` no boot = interrompido — failed + worker_restart
        // (re-execução cega podia duplicar efeitos externos).
        var runId = await SeedRunAsync(
            status: ReviewRunStatuses.Running);
        var runner = new RecordingRunner();
        await using var sp = WorkerProvider(runner);
        var worker = new ReviewRunWorker(new ReviewDispatcher(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            new NullDistributedLock(),
            NullLogger<ReviewRunWorker>.Instance);

        using var cts = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));
        await worker.StartAsync(cts.Token);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (true)
            {
                _db.ChangeTracker.Clear();
                var run = await _db.ReviewRuns.IgnoreQueryFilters()
                    .SingleAsync(r => r.Id == runId);
                if (run.Status == ReviewRunStatuses.Failed)
                {
                    run.Error.ShouldBe("worker_restart");
                    run.FinishedAt.ShouldNotBeNull();
                    break;
                }
                (DateTime.UtcNow < deadline).ShouldBeTrue(
                    "interrupted run was not reconciled");
                await Task.Delay(50, cts.Token);
            }
            runner.Calls.ShouldBeEmpty();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Scopes do worker: DbContext scoped na mesma conexão do teste +
    /// runner fake — espelha o grafo de DI real.
    /// </summary>
    private ServiceProvider WorkerProvider(IReviewRunner runner)
    {
        var sc = new ServiceCollection();
        sc.AddDbContext<AppDbContext>(o => o.UseSqlite(_conn)
            .UseSnakeCaseNamingConvention());
        sc.AddScoped(_ => runner);
        return sc.BuildServiceProvider();
    }

    private sealed class RecordingRunner : IReviewRunner
    {
        private readonly TaskCompletionSource<Guid> _next =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<Guid> Calls { get; } = [];
        public Task<Guid> Next => _next.Task;

        public Task RunAsync(Guid reviewRunId, CancellationToken ct)
        {
            lock (Calls)
            {
                Calls.Add(reviewRunId);
            }
            _next.TrySetResult(reviewRunId);
            return Task.CompletedTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    // ---- fakes ------------------------------------------------------------

    private sealed class FakeProvider : IScmProvider
    {
        public List<ScmPullRequestFile> Files =
        [
            new ScmPullRequestFile("src/Foo.cs", null, "modified",
                2, 1, Patch)
        ];
        public Exception? FilesError;
        public ScmReviewResult? ReviewResult =
            new(123, "https://gh/review/123", 1);

        public List<(string HeadSha, string Body,
            IReadOnlyList<ScmReviewComment> Comments)> Reviews = [];
        public List<(int Number, string Body)> IssueComments = [];
        public List<(string Sha, string State, string Context,
            string? Description)> Statuses = [];

        public ScmKind Kind => ScmKind.GitHub;

        public Task<IReadOnlyList<ScmPullRequestFile>>
            GetPullRequestFilesAsync(ScmConnection connection,
                string repository, int pullRequestNumber,
                CancellationToken ct) =>
            FilesError is not null
                ? Task.FromException<IReadOnlyList<ScmPullRequestFile>>(
                    FilesError)
                : Task.FromResult<IReadOnlyList<ScmPullRequestFile>>(
                    Files);

        public Task<ScmPullRequest?> GetPullRequestAsync(
            ScmConnection connection, string repository, int number,
            CancellationToken ct) =>
            Task.FromResult<ScmPullRequest?>(new ScmPullRequest(
                "https://gh/pr/7", number, "open", "head-1",
                "demo pr"));

        public Task<string?> GetFileContentAsync(ScmConnection connection,
            string repository, string path, string? reference,
            CancellationToken ct) =>
            Task.FromResult<string?>(path == "AGENTS.md"
                ? "# rules\nbe strict" : null);

        public Task<IReadOnlyList<string>> ListDirectoryAsync(
            ScmConnection connection, string repository,
            string directoryPath, string? reference,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<ScmReviewResult?> CreateReviewAsync(
            ScmConnection connection, string repository,
            int pullRequestNumber, string headSha, string body,
            IReadOnlyList<ScmReviewComment> comments,
            CancellationToken ct)
        {
            if (ReviewResult is null)
            {
                return Task.FromResult<ScmReviewResult?>(null);
            }
            Reviews.Add((headSha, body, comments));
            return Task.FromResult<ScmReviewResult?>(ReviewResult);
        }

        public Task<long> CreateIssueCommentAsync(ScmConnection connection,
            string repository, int issueNumber, string body,
            CancellationToken ct)
        {
            IssueComments.Add((issueNumber, body));
            return Task.FromResult(1L);
        }

        public Task SetCommitStatusAsync(ScmConnection connection,
            string repository, string sha, string state, string context,
            string? targetUrl, string? description, CancellationToken ct)
        {
            Statuses.Add((sha, state, context, description));
            return Task.CompletedTask;
        }

        public Task<ScmPullRequest> CreatePullRequestAsync(
            ScmConnection connection, string repository,
            ScmPrCreateRequest request, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<long> ReplyToReviewCommentAsync(
            ScmConnection connection, string repository,
            int pullRequestNumber, long inReplyToCommentId, string body,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<ScmConnectionDiagnostic> TestConnectionAsync(
            ScmConnection connection, CancellationToken ct) =>
            throw new NotImplementedException();

        // SPEC-141: CI autofix members (v1 fake — not used).
        public Task<IReadOnlyList<ScmCheckRun>> GetCheckRunsAsync(
            ScmConnection connection, string repository, string reference,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ScmCheckRun>>([]);

        public Task<string?> GetCheckRunLogsAsync(
            ScmConnection connection, string repository, long checkRunId,
            CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> GetWorkflowRunLogsAsync(
            ScmConnection connection, string repository, long workflowRunId,
            CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<ScmCommitPullRequest>>
            GetPullRequestsForCommitAsync(
                ScmConnection connection, string repository, string sha,
                CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ScmCommitPullRequest>>([]);
    }

    private sealed class FakeRegistry(IScmProvider provider)
        : IScmProviderRegistry
    {
        public IScmProvider? Get(ScmKind kind) =>
            kind == ScmKind.GitHub ? provider : null;
    }

    private sealed class FakeRouter : IModelRouter
    {
        public Task<ModelSelection> SelectAsync(Guid conversationId,
            ModelRole role, string? preferredModel,
            CancellationToken ct) =>
            Task.FromResult(new ModelSelection(
                "review-model", "fake", 128_000, 1, null));

        public Task<ModelSelection?> NextFallbackAsync(
            ModelSelection current, LlmErrorKind kind,
            CancellationToken ct) =>
            Task.FromResult<ModelSelection?>(null);
    }

    private sealed class FakeUsage : IUsageMeter
    {
        public long Tokens;

        public Task RecordTokensAsync(Guid orgId, long tokens,
            decimal? costUsd, CancellationToken ct)
        {
            Tokens += tokens;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDispatcher : IReviewDispatcher
    {
        public List<Guid> Enqueued = [];

        public ValueTask EnqueueAsync(Guid reviewRunId,
            CancellationToken ct)
        {
            Enqueued.Add(reviewRunId);
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

        public Task<(IReadOnlyList<AuditEntry> Items, int Total)>
            QueryAsync(string? userId, string? action,
                DateTimeOffset? from, DateTimeOffset? to, int page,
                int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<AuditEntry>, int)>(([], 0));
    }
}