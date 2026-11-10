using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Contracts;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Events;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Scm;
using Sandcastle.Domain.Conversations;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Scm;
using Sandcastle.Infrastructure.Scm.Review;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Scm;

/// <summary>
/// SPEC-140 — auto-review de PR: roteamento do webhook
/// <c>pull_request</c> (enrollment, draft, label gate, dedup), índice de
/// linhas do diff, parser/validação de findings e commit status.
/// </summary>
public sealed class Spec140PrReviewTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly Guid _orgId = Guid.NewGuid();
    private readonly CapturingReviews _reviews = new();
    private readonly CapturingAudit _audit = new();
    private readonly FakeSecrets _secrets = new();
    private readonly GitHubWebhookService _webhook;
    private readonly Guid _connectionId = Guid.NewGuid();

    public Spec140PrReviewTests()
    {
        _conn.Open();
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
        _webhook = new GitHubWebhookService(_db, _secrets,
            new NullConversations(), _audit,
            NullLogger<GitHubWebhookService>.Instance,
            _reviews,
            Options.Create(new ReviewOptions()));
        SeedConnection(reviewEnabled: true);
        _secrets.Value = "hooksecret";
    }

    private void SeedConnection(bool reviewEnabled,
        string? triggerMode = null, string? reviewRepos = null)
    {
        _db.ScmConnections.Add(new ScmConnectionRecord
        {
            Id = _connectionId,
            OrgId = _orgId,
            Kind = "github",
            InstallationId = "777",
            PemSecretRef = "pem_ref",
            WebhookSecretRef = "hook_ref",
            AppSlug = "sandcastle-agent",
            ReviewEnabled = reviewEnabled,
            ReviewTriggerMode = triggerMode,
            ReviewReposJson = reviewRepos,
            CreatedByUserId = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
    }

    private void UpdateConnection(Action<ScmConnectionRecord> mutate)
    {
        var rec = _db.ScmConnections.IgnoreQueryFilters()
            .Single(c => c.Id == _connectionId);
        mutate(rec);
        _db.SaveChanges();
    }

    private static string Sign(byte[] payload) =>
        $"sha256={Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("hooksecret"), payload))}";

    private static byte[] PrPayload(string action, bool draft = false,
        string sha = "head-1", string[]? labels = null,
        string? requestedReviewer = null)
    {
        var labelsJson = labels is null
            ? "[]"
            : "[" + string.Join(",",
                labels.Select(l => $"{{\"name\":\"{l}\"}}")) + "]";
        var reviewerJson = requestedReviewer is null
            ? ""
            : $",\"requested_reviewer\":{{\"login\":\"{requestedReviewer}\"}}";
        return Encoding.UTF8.GetBytes(
            $"{{\"action\":\"{action}\",\"installation\":{{\"id\":777}},"
            + "\"repository\":{\"full_name\":\"afonsoft/demo\"},"
            + $"\"pull_request\":{{\"number\":7,\"draft\":{(draft ? "true" : "false")},"
            + $"\"head\":{{\"sha\":\"{sha}\"}},\"labels\":{labelsJson}}}"
            + reviewerJson + "}");
    }

    private Task<ScmWebhookResult> Fire(byte[] payload, string delivery) =>
        _webhook.HandleGitHubAsync(Sign(payload), "pull_request",
            delivery, payload, CancellationToken.None);

    // ---- RF-001: trigger routing ----------------------------------------

    [Fact]
    public async Task Pull_request_opened_triggers_review_when_enrolled()
    {
        var result = await Fire(PrPayload("opened"), "d-1");
        result.ShouldBe(ScmWebhookResult.Processed);
        var call = _reviews.Calls.ShouldHaveSingleItem();
        call.Repository.ShouldBe("afonsoft/demo");
        call.PrNumber.ShouldBe(7);
        call.HeadSha.ShouldBe("head-1");
        call.Trigger.ShouldBe("webhook");
        _audit.Actions.ShouldContain("scm.review_triggered");
    }

    [Fact]
    public async Task Pull_request_synchronize_triggers_review()
    {
        var result = await Fire(PrPayload("synchronize", sha: "head-2"),
            "d-2");
        result.ShouldBe(ScmWebhookResult.Processed);
        _reviews.Calls.Single().HeadSha.ShouldBe("head-2");
    }

    [Fact]
    public async Task Pull_request_ignored_when_review_disabled()
    {
        UpdateConnection(c => c.ReviewEnabled = false);
        var result = await Fire(PrPayload("opened"), "d-3");
        result.ShouldBe(ScmWebhookResult.Ignored);
        _reviews.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pull_request_draft_is_ignored()
    {
        var result = await Fire(PrPayload("opened", draft: true), "d-4");
        result.ShouldBe(ScmWebhookResult.Ignored);
        _reviews.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pull_request_closed_is_ignored()
    {
        var result = await Fire(PrPayload("closed"), "d-5");
        result.ShouldBe(ScmWebhookResult.Ignored);
        _reviews.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Label_only_mode_requires_gate_label()
    {
        UpdateConnection(c => c.ReviewTriggerMode = "label_only");
        var without = await Fire(PrPayload("opened"), "d-6");
        without.ShouldBe(ScmWebhookResult.Ignored);

        var withLabel = await Fire(
            PrPayload("labeled", labels: ["sandcastle-review"]), "d-7");
        withLabel.ShouldBe(ScmWebhookResult.Processed);
    }

    [Fact]
    public async Task Label_only_allows_opened_with_gate_label()
    {
        UpdateConnection(c => c.ReviewTriggerMode = "label_only");
        var result = await Fire(
            PrPayload("opened", labels: ["sandcastle-review"]), "d-8");
        result.ShouldBe(ScmWebhookResult.Processed);
    }

    [Fact]
    public async Task Reviewer_request_for_our_bot_triggers()
    {
        var result = await Fire(
            PrPayload("review_requested",
                requestedReviewer: "sandcastle-agent[bot]"), "d-9");
        result.ShouldBe(ScmWebhookResult.Processed);
        _reviews.Calls.Single().Trigger.ShouldBe("reviewer_request");
    }

    [Fact]
    public async Task Reviewer_request_for_other_bot_is_ignored()
    {
        var result = await Fire(
            PrPayload("review_requested",
                requestedReviewer: "dependabot[bot]"), "d-10");
        result.ShouldBe(ScmWebhookResult.Ignored);
    }

    [Fact]
    public async Task Review_repo_allowlist_filters_repository()
    {
        UpdateConnection(c => c.ReviewReposJson =
            "[\"afonsoft/other\"]");
        var result = await Fire(PrPayload("opened"), "d-11");
        result.ShouldBe(ScmWebhookResult.Ignored);
        _reviews.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Duplicate_head_dedup_maps_to_ignored()
    {
        _reviews.NextError = ReviewTriggerError.Duplicate;
        var result = await Fire(PrPayload("opened"), "d-12");
        result.ShouldBe(ScmWebhookResult.Ignored);
    }

    [Fact]
    public async Task Mention_from_own_bot_is_ignored()
    {
        var payload = Encoding.UTF8.GetBytes(
            "{\"action\":\"created\",\"installation\":{\"id\":777},"
            + "\"repository\":{\"full_name\":\"afonsoft/demo\"},"
            + "\"issue\":{\"number\":4},"
            + "\"comment\":{\"id\":99,\"body\":\"@sandcastle hi\","
            + "\"user\":{\"login\":\"sandcastle-agent[bot]\"}}}");
        var result = await _webhook.HandleGitHubAsync(
            Sign(payload), "issue_comment", "d-13", payload,
            CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.Ignored);
    }

    // ---- RF-002: DiffIndex -----------------------------------------------

    private const string Patch = """
        @@ -1,3 +1,5 @@ class Foo {
         line1
        -old
        +newA
        +newB
         line2
        """;

    private static ScmPullRequestFile File(
        string path, string? patch = Patch, int adds = 2) =>
        new(path, null, "modified", adds, 1, patch);

    [Fact]
    public void Diff_index_marks_new_side_lines_as_anchorable()
    {
        var idx = DiffIndex.Build([File("src/Foo.cs")]);
        idx.Paths.ShouldContain("src/Foo.cs");
        idx.IsAnchorable("src/Foo.cs", 2).ShouldBeTrue();  // context
        idx.IsAnchorable("src/Foo.cs", 3).ShouldBeTrue();  // +newA
        idx.IsAnchorable("src/Foo.cs", 4).ShouldBeTrue();  // +newB
        idx.IsAnchorable("src/Foo.cs", 99).ShouldBeFalse();
        idx.IsAnchorable("src/Other.cs", 1).ShouldBeFalse();
    }

    [Fact]
    public void Diff_hash_is_order_independent_and_stable()
    {
        var a = DiffIndex.HashOf([File("b.cs"), File("a.cs")]);
        var b = DiffIndex.HashOf([File("a.cs"), File("b.cs")]);
        a.ShouldBe(b);
        a.Length.ShouldBe(64);
        DiffIndex.HashOf([File("a.cs")]).ShouldNotBe(a);
    }

    [Fact]
    public void Selection_prioritizes_non_generated_and_marks_partial()
    {
        var files = new[]
        {
            File("package-lock.json", adds: 9000),
            File("src/Real.cs", adds: 5),
        };
        var (selected, partial) = DiffIndex.SelectForReview(
            files, maxFiles: 1, maxDiffBytes: 1 << 20);
        selected.ShouldHaveSingleItem().Path.ShouldBe("src/Real.cs");
        partial.ShouldBeTrue();
    }

    [Fact]
    public void Selection_respects_diff_byte_cap()
    {
        var big = File("big.cs",
            patch: "+" + new string('x', 5000), adds: 5000);
        var (selected, partial) = DiffIndex.SelectForReview(
            [big], maxFiles: 10, maxDiffBytes: 100);
        selected.ShouldBeEmpty();
        partial.ShouldBeTrue();
    }

    [Fact]
    public void Generated_heuristic_covers_lockfiles_and_minified()
    {
        DiffIndex.IsProbablyGenerated(
            File("frontend/dist/app.min.js", adds: 3)).ShouldBeTrue();
        DiffIndex.IsProbablyGenerated(
            File("Cargo.lock", adds: 3)).ShouldBeTrue();
        DiffIndex.IsProbablyGenerated(
            File("src/Hand.cs", adds: 3)).ShouldBeFalse();
    }

    // ---- RF-002: finding parsing + validation ----------------------------

    [Fact]
    public void Parser_keeps_valid_finding_and_normalizes_enums()
    {
        var idx = DiffIndex.Build([File("src/Foo.cs")]);
        var llm = """
            {"findings":[
              {"path":"src/Foo.cs","line":3,"severity":"major",
               "kind":"correctness","title":"Null deref",
               "body":"x can be null","confidence":1.7}
            ]}
            """;
        var (valid, dropped) = ReviewFindingParser.Parse(llm, idx);
        dropped.ShouldBe(0);
        var f = valid.ShouldHaveSingleItem();
        f.Severity.ShouldBe(ReviewSeverities.Critical);
        f.Kind.ShouldBe(ReviewFindingKinds.Bug);
        f.Confidence.ShouldBe(1.0);
        f.Line.ShouldBe(3);
    }

    [Fact]
    public void Parser_drops_path_outside_diff()
    {
        var idx = DiffIndex.Build([File("src/Foo.cs")]);
        var llm = """
            {"findings":[
              {"path":"src/Elsewhere.cs","line":3,"body":"hallucinated"},
              {"path":"src/Foo.cs","line":3,"body":"real"}
            ]}
            """;
        var (valid, dropped) = ReviewFindingParser.Parse(llm, idx);
        dropped.ShouldBe(1);
        valid.ShouldHaveSingleItem().Path.ShouldBe("src/Foo.cs");
    }

    [Fact]
    public void Parser_drops_line_not_on_new_side()
    {
        var idx = DiffIndex.Build([File("src/Foo.cs")]);
        var llm = """
            {"findings":[
              {"path":"src/Foo.cs","line":77,"body":"out of patch"},
              {"path":"src/Foo.cs","body":"file-level ok"}
            ]}
            """;
        var (valid, dropped) = ReviewFindingParser.Parse(llm, idx);
        dropped.ShouldBe(1);
        valid.ShouldHaveSingleItem().Line.ShouldBeNull();
    }

    [Fact]
    public void Parser_returns_empty_on_non_json_output()
    {
        var idx = DiffIndex.Build([File("src/Foo.cs")]);
        var (valid, dropped) = ReviewFindingParser.Parse(
            "no json here", idx);
        valid.ShouldBeEmpty();
        dropped.ShouldBe(0);
    }

    [Fact]
    public void Parser_normalizes_unknown_severity_to_suggestion()
    {
        var idx = DiffIndex.Build([File("src/Foo.cs")]);
        var llm = """
            {"findings":[{"path":"src/Foo.cs","line":3,
               "severity":"cosmetic","kind":"naming","body":"x"}]}
            """;
        var (valid, _) = ReviewFindingParser.Parse(llm, idx);
        var f = valid.ShouldHaveSingleItem();
        f.Severity.ShouldBe(ReviewSeverities.Suggestion);
        f.Kind.ShouldBe(ReviewFindingKinds.Style);
    }

    // ---- RF-003: formatter + commit status --------------------------------

    private static ParsedFinding Finding(string sev, int? line = 3) =>
        new("src/Foo.cs", line, sev, ReviewFindingKinds.Bug,
            null, "body", 0.9, null);

    [Fact]
    public void Summary_lists_grouped_counts()
    {
        var body = ReviewFormatter.Summary(
            [Finding(ReviewSeverities.Critical),
             Finding(ReviewSeverities.Critical),
             Finding(ReviewSeverities.Nit)],
            [], filesReviewed: 3, filesTotal: 3,
            partial: false, inlineAnchored: true);
        body.ShouldContain("3 findings");
        body.ShouldContain("2 critical");
        body.ShouldContain("1 nit");
        body.ShouldContain("sandcastle/review");
    }

    [Fact]
    public void Summary_zero_findings_is_clean()
    {
        var body = ReviewFormatter.Summary(
            [], [], 2, 2, partial: false, inlineAnchored: true);
        body.ShouldContain("No issues found");
    }

    [Fact]
    public void Summary_renders_overflow_as_details()
    {
        var body = ReviewFormatter.Summary(
            [Finding(ReviewSeverities.Nit)],
            [Finding(ReviewSeverities.Suggestion, line: 4)],
            1, 1, partial: false, inlineAnchored: true);
        body.ShouldContain("beyond the inline comment cap");
    }

    [Fact]
    public void Commit_status_default_is_always_success()
    {
        var (state, _) = ReviewFormatter.CommitStatus(
            ReviewDefaults.PassConditionCompleted,
            [Finding(ReviewSeverities.Critical)], partial: false);
        state.ShouldBe("success");
    }

    [Fact]
    public void Commit_status_no_critical_fails_on_critical()
    {
        var (state, desc) = ReviewFormatter.CommitStatus(
            ReviewDefaults.PassConditionNoCritical,
            [Finding(ReviewSeverities.Critical)], partial: false);
        state.ShouldBe("failure");
        desc.ShouldContain("advisory");

        ReviewFormatter.CommitStatus(
            ReviewDefaults.PassConditionNoCritical,
            [Finding(ReviewSeverities.Nit)], partial: false)
            .State.ShouldBe("success");
    }

    [Fact]
    public void Commit_status_no_findings_fails_on_any_finding()
    {
        ReviewFormatter.CommitStatus(
            ReviewDefaults.PassConditionNoFindings,
            [Finding(ReviewSeverities.Nit)], partial: false)
            .State.ShouldBe("failure");
        ReviewFormatter.CommitStatus(
            ReviewDefaults.PassConditionNoFindings,
            [], partial: false).State.ShouldBe("success");
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    // ---- fakes -------------------------------------------------------------

    private sealed class CapturingReviews : IReviewService
    {
        public List<(string Repository, int PrNumber, string HeadSha,
            string Trigger)> Calls = [];
        public ReviewTriggerError? NextError;

        public Task<ReviewTriggerResult> TriggerAsync(
            ScmConnection connection, string repository, int prNumber,
            string headSha, string trigger, string? requestedByUserId,
            CancellationToken ct)
        {
            if (NextError is { } e)
            {
                return Task.FromResult(
                    ReviewTriggerResult.Fail(e, "nope"));
            }
            Calls.Add((repository, prNumber, headSha, trigger));
            return Task.FromResult(
                ReviewTriggerResult.Ok(Guid.NewGuid()));
        }

        public Task<ReviewRunPage> SearchAsync(Guid orgId,
            string? repository, int? prNumber, string? status,
            string? cursor, int limit, CancellationToken ct) =>
            Task.FromResult(new ReviewRunPage([], null));

        public Task<ReviewRunDetail?> GetAsync(Guid orgId, Guid id,
            CancellationToken ct) =>
            Task.FromResult<ReviewRunDetail?>(null);

        public Task<ReviewFinding?> UpdateFindingStatusAsync(Guid orgId,
            Guid runId, Guid findingId, string status,
            CancellationToken ct) =>
            Task.FromResult<ReviewFinding?>(null);
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

    private sealed class FakeSecrets : ISecretStore
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

    private sealed class NullConversations
        : Sandcastle.Application.Conversations.IAppConversationService
    {
        public Task<AppConversationStartTask> CreateAsync(
            CreateConversationRequest request, CancellationToken ct) =>
            Task.FromResult(Task0());

        public Task<AppConversationStartTask> CreateForUserAsync(
            Guid userId, Guid? orgId, CreateConversationRequest request,
            CancellationToken ct) => Task.FromResult(Task0());

        private static AppConversationStartTask Task0() => new(
            Guid.NewGuid(), StartTaskStatus.Working, Guid.NewGuid(),
            null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        public Task<ConversationPage> SearchAsync(ConversationQuery query,
            CancellationToken ct) => throw new NotImplementedException();
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
        public Task<AttentionDismissResult> DismissAttentionAsync(Guid id,
            CancellationToken ct) => throw new NotImplementedException();
    }
}