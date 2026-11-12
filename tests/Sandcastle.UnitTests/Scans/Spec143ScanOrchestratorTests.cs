using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Delegation;
using Sandcastle.Application.Indexing;
using Sandcastle.Application.Scans;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Delegation;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Scans;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Scans;

/// <summary>
/// SPEC-143 RF-002: <see cref="ScanOrchestrator"/> — partitions a repo
/// checkout, fans out <c>scan-worker</c> children via
/// <see cref="IDelegationService"/>, collects <c>scan_finding_v1</c>
/// outputs, dedups and persists findings + stats_json. Also verifies the
/// delegation-side read-only enforcement (children inherit the parent
/// deny-list, never relax).
/// </summary>
public sealed class Spec143ScanOrchestratorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"scan-orch-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteRepo(params string[] files)
    {
        foreach (var rel in files)
        {
            var path = Path.Combine(_root,
                rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "// x\n");
        }
    }

    private static ScanOrchestrator NewOrchestrator(
        AppDbContext db, IDelegationService delegation,
        IRepoContentProvider content,
        ScanOptions? options = null) =>
        new(db, delegation, content,
            new FakeScanLlm(), new FakeScanRouter(),
            Options.Create(options ?? new ScanOptions()),
            NullLogger<ScanOrchestrator>.Instance);

    private static string FindingsPayload(params object[] findings) =>
        "analysis complete\n```scan_findings\n"
        + JsonSerializer.Serialize(
            new { schema = "scan_finding_v1", findings })
        + "\n```";

    [Fact]
    public async Task Run_FanOut_Collects_Dedups_Persists()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        WriteRepo("src/A.cs", "src/B.cs", "tests/T.cs", "docs/D.md");
        var org = Guid.NewGuid();
        var scan = await ScanTestDb.SeedScanAsync(
            db, org, Guid.NewGuid(), ["org/repo"], effort: "low");

        var payload = FindingsPayload(
            new
            {
                path = "src/A.cs",
                start_line = 10,
                kind = "security",
                severity = "high",
                title = "sqli",
                description = "user input reaches sql",
            },
            new
            {
                path = "src/A.cs",
                start_line = 10,
                kind = "security",
                severity = "low",
                title = "sqli dup",
                description = "duplicate row merges into the high one",
            },
            new
            {
                path = "src/B.cs",
                kind = "dead_code",
                severity = "info",
                description = "unused method",
            });

        var delegation = new FakeDelegation(db, payload);
        var content = new FakeRepoContentProvider(_root);
        var orch = NewOrchestrator(db, delegation, content,
            new ScanOptions { PollIntervalSeconds = 2 });

        await orch.RunAsync(scan.Id, CancellationToken.None);

        var done = await db.Scans.FindAsync(scan.Id);
        done!.Status.ShouldBe(ScanStatuses.Completed);
        done.FinishedAt.ShouldNotBeNull();
        done.StatsJson.ShouldNotBeNull();

        // Fan-out: todas as filhas são scan-worker com repo do scan.
        delegation.Calls.ShouldNotBeEmpty();
        delegation.Calls.ShouldAllBe(
            c => c.Req.AgentType == ScanAgentTypes.Worker);
        delegation.Calls.ShouldAllBe(
            c => c.Req.SelectedRepository == "org/repo");
        delegation.Calls.ShouldAllBe(
            c => c.Parent == done.CoordinatorConversationId);

        // Dedup: 3 raw → 2 persisted (exact merge).
        var findings = await db.ScanFindings.ToListAsync();
        findings.Count.ShouldBe(2);
        findings.ShouldContain(
            f => f.Severity == "high" && f.Path == "src/A.cs");
        findings.ShouldAllBe(
            f => f.Status == ScanFindingStatuses.Open
                && f.RemediationState
                    == ScanRemediationStates.Unassigned
                && f.ScanId == scan.Id && f.OrgId == org);
    }

    [Fact]
    public async Task Run_No_Partitionable_Repo_Fails()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var org = Guid.NewGuid();
        var scan = await ScanTestDb.SeedScanAsync(
            db, org, Guid.NewGuid(), ["org/repo"]);
        var content = new FakeRepoContentProvider(_root)
        { ThrowOnAcquire = new InvalidOperationException("no access") };
        var orch = NewOrchestrator(
            db, new FakeDelegation(db), content);

        await orch.RunAsync(scan.Id, CancellationToken.None);

        var done = await db.Scans.FindAsync(scan.Id);
        done!.Status.ShouldBe(ScanStatuses.Failed);
        done.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task Run_Terminal_Scan_Is_Idempotent()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var scan = await ScanTestDb.SeedScanAsync(db, Guid.NewGuid(),
            Guid.NewGuid(), status: ScanStatuses.Completed);
        var delegation = new FakeDelegation(db);
        var orch = NewOrchestrator(
            db, delegation, new FakeRepoContentProvider(_root));

        await orch.RunAsync(scan.Id, CancellationToken.None);

        delegation.Calls.ShouldBeEmpty();
    }

    /// <summary>
    /// SPEC-143 §9: scan children are read-only — DelegationService must
    /// copy the parent's deny rules verbatim into the child create
    /// request (permissions never relax on delegation).
    /// </summary>
    [Fact]
    public async Task Delegate_Inherits_Parent_ReadOnly_Rules()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var convs = new FakeConversationService(db);
        var svc = new DelegationService(db, convs,
            NullLogger<DelegationService>.Instance);
        var org = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        db.Conversations.Add(new ConversationRecord
        {
            Id = parentId,
            ExecutionStatus = "Running",
            OrgId = org,
            AgentType = ScanAgentTypes.Coordinator,
            SelectedRepository = "org/repo",
            PermissionsJson = PermissionRules
                .ToJson(ScanPermissions.ReadOnly),
            CreatedByUserId = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var res = await svc.DelegateAsync(parentId,
            new DelegateTaskRequest(
                "scan partition", "org/repo", ScanAgentTypes.Worker),
            CancellationToken.None);

        res.ChildConversationId.ShouldNotBe(Guid.Empty);
        var req = convs.LastRequest!;
        req.Permissions.ShouldNotBeNull();
        // Toda a deny-list do pai chega intacta à filha.
        req.Permissions!.Count.ShouldBe(ScanPermissions.ReadOnly.Count);
        req.Permissions.ShouldAllBe(r =>
            r.Decision == PermissionDecision.Deny);
        req.Permissions.ShouldContain(r => r.Pattern == "git_commit");
        req.Permissions.ShouldContain(r => r.Pattern == "file_write");
        req.Permissions.ShouldContain(
            r => r.Pattern == "create_pull_request");
        req.Permissions.ShouldContain(r => r.Pattern == "bash");
    }

    /// <summary>
    /// SPEC-143 RF-002: wave fan-out — o cap de siblings conta só filhos
    /// ATIVOS; filhos terminados liberam slots (o orquestrador reusa a
    /// wave em vez de estourar o limite).
    /// </summary>
    [Fact]
    public async Task Sibling_Cap_Counts_Only_Active_Children()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var convs = new FakeConversationService(db);
        var svc = new DelegationService(db, convs,
            NullLogger<DelegationService>.Instance);
        var parentId = Guid.NewGuid();
        db.Conversations.Add(new ConversationRecord
        {
            Id = parentId,
            ExecutionStatus = "Running",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        // 8 filhos JÁ TERMINADOS — não bloqueiam a wave seguinte.
        for (var i = 0; i < 8; i++)
        {
            db.Conversations.Add(new ConversationRecord
            {
                Id = Guid.NewGuid(),
                ParentConversationId = parentId,
                ExecutionStatus = "Finished",
                DelegationDepth = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();

        var res = await svc.DelegateAsync(parentId,
            new DelegateTaskRequest("next wave"), CancellationToken.None);
        res.ChildConversationId.ShouldNotBe(Guid.Empty);
    }

    /// <summary>
    /// Cap real: 8 filhos ativos bloqueiam o 9º delegate.
    /// </summary>
    [Fact]
    public async Task Sibling_Cap_Blocks_When_All_Active()
    {
        var (conn, db) = await ScanTestDb.NewAsync();
        await using var _ = conn;
        var convs = new FakeConversationService(db);
        var svc = new DelegationService(db, convs,
            NullLogger<DelegationService>.Instance);
        var parentId = Guid.NewGuid();
        db.Conversations.Add(new ConversationRecord
        {
            Id = parentId,
            ExecutionStatus = "Running",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        for (var i = 0; i < 8; i++)
        {
            db.Conversations.Add(new ConversationRecord
            {
                Id = Guid.NewGuid(),
                ParentConversationId = parentId,
                ExecutionStatus = "Running", // ativo → conta no cap
                DelegationDepth = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            svc.DelegateAsync(parentId,
                new DelegateTaskRequest("overflow"),
                CancellationToken.None));
    }
}