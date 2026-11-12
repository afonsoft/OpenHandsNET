using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Identity;
using Sandcastle.Application.Scans;
using Sandcastle.Application.Scm;
using Sandcastle.Domain.Conversations;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Scans;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Scans;

/// <summary>
/// SPEC-143: <see cref="ScanService"/> — creation (coordinator conv +
/// read-only perms + enqueue), org-scoped reads, cursor pagination,
/// finding triage state machine, fix→conversation wiring (mocked
/// delegate/conversation service) and remediation reconciliation
/// (unassigned → assigned → pr_open → merged / back to unassigned).
/// </summary>
public sealed class Spec143ScanServiceTests
{
    private static ScanService NewService(
        AppDbContext db, FakeConversationService convs,
        FakeScanDispatcher dispatcher,
        RecordingScanAudit? audit = null,
        ScanOptions? options = null,
        IScmConnectionService? connections = null,
        IScmProviderRegistry? providers = null) =>
        new(db, convs, dispatcher,
            Options.Create(options ?? new ScanOptions()),
            NullLogger<ScanService>.Instance,
            audit, connections, providers);

    private static ScanFindingRecord SeedFinding(
        AppDbContext db, Guid orgId, Guid scanId,
        string status = ScanFindingStatuses.Open,
        string remediation = ScanRemediationStates.Unassigned,
        string kind = "security", string severity = "high",
        string path = "src/A.cs", string repo = "org/repo",
        int? line = 10, Guid? convId = null, string? prUrl = null)
    {
        var f = new ScanFindingRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            ScanId = scanId,
            Repository = repo,
            Path = path,
            StartLine = line,
            Kind = kind,
            Severity = severity,
            Title = "issue",
            Description = "desc",
            Status = status,
            RemediationState = remediation,
            ConversationId = convId,
            PrUrl = prUrl,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedAtTicks = DateTimeOffset.UtcNow.UtcTicks,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.ScanFindings.Add(f);
        return f;
    }

    // ---- create ---------------------------------------------------------

    [Fact]
    public async Task Create_Validates_Goal_Profile_Effort_Repos()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var svc = NewService(db, new FakeConversationService(),
            new FakeScanDispatcher());
        var org = Guid.NewGuid();
        var owner = Guid.NewGuid();

        (await svc.CreateAsync(
            new CreateScanRequest(["org/repo"], " ", null, null),
            owner, org, ScanTriggers.Manual, CancellationToken.None))
            .Error.ShouldBe("goal is required");
        (await svc.CreateAsync(
            new CreateScanRequest(["org/repo"], "g", "bogus", null),
            owner, org, ScanTriggers.Manual, CancellationToken.None))
            .Error!.ShouldContain("invalid profile");
        (await svc.CreateAsync(
            new CreateScanRequest(["org/repo"], "g", null, "extreme"),
            owner, org, ScanTriggers.Manual, CancellationToken.None))
            .Error!.ShouldContain("invalid effort");
        (await svc.CreateAsync(
            new CreateScanRequest([], "g", null, null),
            owner, org, ScanTriggers.Manual, CancellationToken.None))
            .Error.ShouldBe("at least one repository is required");
    }

    [Fact]
    public async Task Create_Spawns_ReadOnly_Coordinator_And_Enqueues()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var convs = new FakeConversationService(db);
        var dispatcher = new FakeScanDispatcher();
        var audit = new RecordingScanAudit();
        var svc = NewService(db, convs, dispatcher, audit);
        var org = Guid.NewGuid();
        var owner = Guid.NewGuid();

        var (created, error) = await svc.CreateAsync(
            new CreateScanRequest(
                ["org/repo", "org/repo2"], "find sqli",
                "security", "low"),
            owner, org, ScanTriggers.Manual, CancellationToken.None);

        error.ShouldBeNull();
        created.ShouldNotBeNull();
        created!.Status.ShouldBe(ScanStatuses.Queued);
        dispatcher.Enqueued.ShouldBe([created.ScanId]);

        // Coordinator: agent_type + Run:false + deny-list read-only.
        var req = convs.LastRequest!;
        req.AgentType.ShouldBe(ScanAgentTypes.Coordinator);
        req.InitialMessage!.Run.ShouldBeFalse();
        req.SelectedRepository.ShouldBe("org/repo");
        req.Permissions.ShouldNotBeNull();
        req.Permissions!.ShouldContain(
            r => r.Pattern == "git_commit");
        req.Permissions.ShouldContain(
            r => r.Pattern == "file_write");
        req.Permissions.ShouldContain(
            r => r.Pattern == "create_pull_request");
        convs.LastUserId.ShouldBe(owner);
        convs.LastOrgId.ShouldBe(org);
        audit.Actions.ShouldContain("scan.created");
    }

    // ---- list/detail/cursor ----------------------------------------------

    [Fact]
    public async Task List_Is_OrgScoped_And_Cursored()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
        {
            await ScanTestDb.SeedScanAsync(db, orgA, Guid.NewGuid());
        }
        await ScanTestDb.SeedScanAsync(db, orgB, Guid.NewGuid());

        var svc = NewService(db, new FakeConversationService(),
            new FakeScanDispatcher());

        var page1 = await svc.ListAsync(orgA, null, null, 2,
            CancellationToken.None);
        page1.Items.Count.ShouldBe(2);
        page1.NextPageId.ShouldNotBeNull();

        var page2 = await svc.ListAsync(orgA, null, page1.NextPageId, 2,
            CancellationToken.None);
        page2.Items.Count.ShouldBe(1);
        page2.NextPageId.ShouldBeNull();

        var ids = page1.Items.Concat(page2.Items)
            .Select(s => s.Id).ToList();
        ids.Distinct().Count().ShouldBe(3);

        (await svc.ListAsync(orgB, null, null, 50,
            CancellationToken.None)).Items.Count.ShouldBe(1);
    }

    // ---- findings feed ----------------------------------------------------

    [Fact]
    public async Task Findings_Filters_And_Cursor_Work()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var scanId = Guid.NewGuid();
        var f1 = SeedFinding(db, org, scanId, severity: "critical");
        SeedFinding(db, org, scanId, severity: "low",
            kind: "performance", path: "src/B.cs");
        SeedFinding(db, org, Guid.NewGuid()); // outro scan
        await db.SaveChangesAsync();

        var svc = NewService(db, new FakeConversationService(),
            new FakeScanDispatcher());

        // filtro por scan + severity.
        var page = await svc.SearchFindingsAsync(org,
            new ScanFindingQuery(ScanId: scanId, Severity: "critical"),
            CancellationToken.None);
        page.Items.Count.ShouldBe(1);
        page.Items[0].Id.ShouldBe(f1.Id);

        // paginação first=1.
        var p1 = await svc.SearchFindingsAsync(org,
            new ScanFindingQuery(ScanId: scanId, First: 1),
            CancellationToken.None);
        p1.Items.Count.ShouldBe(1);
        p1.NextPageId.ShouldNotBeNull();
        var p2 = await svc.SearchFindingsAsync(org,
            new ScanFindingQuery(ScanId: scanId, First: 1,
                After: p1.NextPageId),
            CancellationToken.None);
        p2.Items.Count.ShouldBe(1);
        (p1.Items[0].Id != p2.Items[0].Id).ShouldBeTrue();

        // org errada não vê nada.
        (await svc.SearchFindingsAsync(Guid.NewGuid(),
            new ScanFindingQuery(), CancellationToken.None))
            .Items.ShouldBeEmpty();
    }

    // ---- triage -----------------------------------------------------------

    [Fact]
    public async Task Triage_Open_Reviewed_Dismissed()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var f = SeedFinding(db, org, Guid.NewGuid());
        await db.SaveChangesAsync();
        var svc = NewService(db, new FakeConversationService(),
            new FakeScanDispatcher(), new RecordingScanAudit());

        (await svc.UpdateFindingStatusAsync(org, f.Id, "reviewed",
            CancellationToken.None))!.Status.ShouldBe("reviewed");
        (await svc.UpdateFindingStatusAsync(org, f.Id, "DISMISSED",
            CancellationToken.None))!.Status.ShouldBe("dismissed");
        (await svc.UpdateFindingStatusAsync(org, f.Id, "open",
            CancellationToken.None))!.Status.ShouldBe("open");
        (await svc.UpdateFindingStatusAsync(org, Guid.NewGuid(), "open",
            CancellationToken.None)).ShouldBeNull();
        await Should.ThrowAsync<ArgumentException>(() =>
            svc.UpdateFindingStatusAsync(org, f.Id, "banana",
                CancellationToken.None));
    }

    // ---- fix wiring -------------------------------------------------------

    [Fact]
    public async Task Fix_Creates_Remediation_Conv_And_Assigns()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var f = SeedFinding(db, org, Guid.NewGuid());
        await db.SaveChangesAsync();
        var convs = new FakeConversationService(db);
        var audit = new RecordingScanAudit();
        var svc = NewService(db, convs, new FakeScanDispatcher(), audit);
        var actor = Guid.NewGuid();

        var r = await svc.FixAsync(org, f.Id, actor,
            CancellationToken.None);

        r.Error.ShouldBeNull();
        r.ConversationId.ShouldNotBeNull();
        // fix conv: agent_type scan-fix + Run:true + repo do finding.
        var req = convs.LastRequest!;
        req.AgentType.ShouldBe(ScanAgentTypes.Fix);
        req.InitialMessage!.Run.ShouldBeTrue();
        req.SelectedRepository.ShouldBe("org/repo");
        var prompt = req.InitialMessage.Content[0]?.ToString() ?? "";
        prompt.ShouldContain("src/A.cs");
        convs.LastUserId.ShouldBe(actor);

        var persisted = await db.ScanFindings.FindAsync(f.Id);
        persisted!.RemediationState
            .ShouldBe(ScanRemediationStates.Assigned);
        persisted.ConversationId.ShouldBe(r.ConversationId);
        audit.Actions.ShouldContain("scan_finding.fix_started");
    }

    [Fact]
    public async Task Fix_Twice_Returns_Fix_In_Progress()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var f = SeedFinding(db, org, Guid.NewGuid(),
            remediation: ScanRemediationStates.Assigned);
        await db.SaveChangesAsync();
        var convs = new FakeConversationService(db);
        var svc = NewService(db, convs, new FakeScanDispatcher());

        var r = await svc.FixAsync(org, f.Id, Guid.NewGuid(),
            CancellationToken.None);

        r.Error.ShouldBe("fix_in_progress");
        convs.Created.ShouldBe(0); // nunca auto-dispara de novo
    }

    [Fact]
    public async Task Fix_Missing_Returns_Not_Found()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var svc = NewService(db, new FakeConversationService(),
            new FakeScanDispatcher());
        var r = await svc.FixAsync(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), CancellationToken.None);
        r.Error.ShouldBe("not_found");
    }

    [Fact]
    public async Task FixBulk_Dispatches_Per_Finding()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var f1 = SeedFinding(db, org, Guid.NewGuid());
        var f2 = SeedFinding(db, org, Guid.NewGuid(), path: "src/B.cs");
        var assigned = SeedFinding(db, org, Guid.NewGuid(),
            remediation: ScanRemediationStates.Assigned,
            path: "src/C.cs");
        await db.SaveChangesAsync();
        var convs = new FakeConversationService(db);
        var svc = NewService(db, convs, new FakeScanDispatcher());

        var results = await svc.FixBulkAsync(org,
            [f1.Id, f2.Id, assigned.Id, Guid.NewGuid()],
            Guid.NewGuid(), CancellationToken.None);

        results.Count(r => r.Error is null).ShouldBe(2);
        results.Single(r => r.FindingId == assigned.Id)
            .Error.ShouldBe("fix_in_progress");
        results.Count(r => r.Error == "not_found").ShouldBe(1);
        convs.Created.ShouldBe(2);
    }

    // ---- remediation state machine ----------------------------------------

    [Fact]
    public async Task Reconcile_Assigned_To_PrOpen_When_Conv_Has_Pr()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var convId = Guid.NewGuid();
        var f = SeedFinding(db, org, Guid.NewGuid(),
            remediation: ScanRemediationStates.Assigned,
            convId: convId);
        db.Conversations.Add(new ConversationRecord
        {
            Id = convId,
            ExecutionStatus = "Finished",
            OrgId = org,
            AgentType = ScanAgentTypes.Fix,
            PullRequestUrl = "https://gh/org/repo/pull/7",
            PullRequestNumber = 7,
            PullRequestState = "open",
            SelectedRepository = "org/repo",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var audit = new RecordingScanAudit();
        var svc = NewService(db, new FakeConversationService(),
            new FakeScanDispatcher(), audit);

        await svc.ReconcileRemediationsAsync(CancellationToken.None);

        var persisted = await db.ScanFindings.FindAsync(f.Id);
        persisted!.RemediationState
            .ShouldBe(ScanRemediationStates.PrOpen);
        persisted.PrUrl.ShouldBe("https://gh/org/repo/pull/7");
        audit.Actions.ShouldContain("scan_finding.pr_open");
    }

    [Fact]
    public async Task Reconcile_Dead_Fix_Conv_Back_To_Unassigned()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var convId = Guid.NewGuid();
        var f = SeedFinding(db, org, Guid.NewGuid(),
            remediation: ScanRemediationStates.Assigned,
            convId: convId);
        db.Conversations.Add(new ConversationRecord
        {
            Id = convId,
            ExecutionStatus = "Error", // morreu sem PR
            OrgId = org,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var svc = NewService(db, new FakeConversationService(),
            new FakeScanDispatcher());

        await svc.ReconcileRemediationsAsync(CancellationToken.None);

        var persisted = await db.ScanFindings.FindAsync(f.Id);
        persisted!.RemediationState
            .ShouldBe(ScanRemediationStates.Unassigned);
        persisted.ConversationId.ShouldBeNull();
    }

    [Fact]
    public async Task Reconcile_PrOpen_To_Merged_Via_Provider()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var convId = Guid.NewGuid();
        var f = SeedFinding(db, org, Guid.NewGuid(),
            remediation: ScanRemediationStates.PrOpen,
            convId: convId, prUrl: "https://gh/org/repo/pull/9");
        db.Conversations.Add(new ConversationRecord
        {
            Id = convId,
            ExecutionStatus = "Finished",
            OrgId = org,
            PullRequestUrl = "https://gh/org/repo/pull/9",
            PullRequestNumber = 9,
            PullRequestState = "closed",
            SelectedRepository = "org/repo",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var provider = new FakePrProvider(merged: true);
        var svc = NewService(db, new FakeConversationService(),
            new FakeScanDispatcher(), new RecordingScanAudit(),
            connections: new FakeConnectionService(),
            providers: new FakePrRegistry(provider));

        await svc.ReconcileRemediationsAsync(CancellationToken.None);

        var persisted = await db.ScanFindings.FindAsync(f.Id);
        persisted!.RemediationState
            .ShouldBe(ScanRemediationStates.Merged);
    }

    [Fact]
    public async Task Reconcile_Closed_Unmerged_Back_To_Unassigned()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var convId = Guid.NewGuid();
        var f = SeedFinding(db, org, Guid.NewGuid(),
            remediation: ScanRemediationStates.PrOpen,
            convId: convId, prUrl: "https://gh/org/repo/pull/9");
        db.Conversations.Add(new ConversationRecord
        {
            Id = convId,
            ExecutionStatus = "Finished",
            OrgId = org,
            PullRequestUrl = "https://gh/org/repo/pull/9",
            PullRequestNumber = 9,
            PullRequestState = "closed",
            SelectedRepository = "org/repo",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var provider = new FakePrProvider(merged: false);
        var svc = NewService(db, new FakeConversationService(),
            new FakeScanDispatcher(),
            connections: new FakeConnectionService(),
            providers: new FakePrRegistry(provider));

        await svc.ReconcileRemediationsAsync(CancellationToken.None);

        var persisted = await db.ScanFindings.FindAsync(f.Id);
        persisted!.RemediationState
            .ShouldBe(ScanRemediationStates.Unassigned);
        persisted.ConversationId.ShouldBeNull();
        persisted.PrUrl.ShouldBeNull();
    }

    // ---- scm fakes ---------------------------------------------------------

    private sealed class FakeConnectionService : IScmConnectionService
    {
        private static readonly ScmConnection Conn = new(
            Guid.NewGuid(), Guid.Empty, ScmKind.GitHub,
            "inst", null, null, ["org/repo"], "app", "slug", "user",
            DateTimeOffset.UtcNow);

        public Task<ScmConnection?> FindForRepositoryAsync(
            Guid orgId, ScmKind kind, string repository,
            CancellationToken ct) =>
            Task.FromResult<ScmConnection?>(Conn);

        public object BuildGitHubAppManifest(
            string redirectUri, string appName) =>
            throw new NotSupportedException();
        public Task<ScmConnection?> CompleteGitHubManifestFlowAsync(
            Guid orgId, string code, string? createdByUserId,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<ScmConnection> CreateAsync(Guid orgId, ScmKind kind,
            string? installationId, string? pemSecretRef,
            string? webhookSecretRef, IReadOnlyList<string>? repos,
            string? appId, string? appSlug, string? createdByUserId,
            CancellationToken ct, string? apiUrl = null,
            string? authKind = null, string? metaJson = null) =>
            throw new NotSupportedException();
        public Task<ScmConnectionDiagnostic> TestAsync(
            Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ScmConnection>> ListAsync(
            Guid orgId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ScmConnection?> GetAsync(
            Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ScmConnection?> UpdateReviewSettingsAsync(
            Guid orgId, Guid id, ScmConnectionReviewUpdate update,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<ScmConnection?> UpdateAutofixSettingsAsync(
            Guid orgId, Guid id, ScmConnectionAutofixUpdate update,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<ScmConnection?> UpdateQaSettingsAsync(
            Guid orgId, Guid id, ScmConnectionQaUpdate update,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(
            Guid orgId, Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakePrProvider(bool merged) : IScmProvider
    {
        public ScmKind Kind => ScmKind.GitHub;

        public Task<ScmPullRequest?> GetPullRequestAsync(
            ScmConnection c, string r, int n, CancellationToken ct) =>
            Task.FromResult<ScmPullRequest?>(new ScmPullRequest(
                "https://gh/pr/9", n, merged ? "merged" : "closed",
                "head-1", "fix", merged));

        public Task<ScmPullRequest> CreatePullRequestAsync(
            ScmConnection c, string r, ScmPrCreateRequest q,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<long> CreateIssueCommentAsync(ScmConnection c,
            string r, int n, string b, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<long> ReplyToReviewCommentAsync(ScmConnection c,
            string r, int p, long i, string b, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task SetCommitStatusAsync(ScmConnection c, string r,
            string s, string st, string ctx, string? t, string? d,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<ScmConnectionDiagnostic> TestConnectionAsync(
            ScmConnection c, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ScmPullRequestFile>>
            GetPullRequestFilesAsync(ScmConnection c, string r, int n,
                CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetFileContentAsync(ScmConnection c,
            string r, string p, string? rf, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListDirectoryAsync(
            ScmConnection c, string r, string p, string? rf,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ScmCheckRun>> GetCheckRunsAsync(
            ScmConnection c, string r, string rf, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<string?> GetCheckRunLogsAsync(ScmConnection c,
            string r, long i, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<string?> GetWorkflowRunLogsAsync(ScmConnection c,
            string r, long i, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ScmCommitPullRequest>>
            GetPullRequestsForCommitAsync(ScmConnection c, string r,
                string s, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ScmReviewResult?> CreateReviewAsync(ScmConnection c,
            string r, int n, string h, string b,
            IReadOnlyList<ScmReviewComment> cs, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakePrRegistry(IScmProvider provider)
        : IScmProviderRegistry
    {
        public IScmProvider? Get(ScmKind kind) =>
            kind == ScmKind.GitHub ? provider : null;
    }
}