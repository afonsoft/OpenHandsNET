using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Sandcastle.Application.Automations;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Scans;
using Sandcastle.Infrastructure.Automations;
using Sandcastle.UnitTests.Agent;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Scans;

/// <summary>
/// SPEC-143 RF-006: automation <c>kind=scan</c> — upsert validation
/// (repositories required, profile/effort validated) and FireAsync
/// dispatch through <see cref="IScanService"/> (not the ordinary
/// conversation path).
/// </summary>
public sealed class Spec143AutomationScanTests
{
    private sealed class FakeScanService : IScanService
    {
        public List<(CreateScanRequest Req, Guid Owner, Guid? Org,
            string Trigger)> Calls
        { get; } = [];
        public string? FailWith { get; set; }
        public Guid CoordinatorId { get; } = Guid.NewGuid();

        public Task<(ScanCreated? Created, string? Error)> CreateAsync(
            CreateScanRequest request, Guid ownerUserId, Guid? orgId,
            string trigger, CancellationToken ct)
        {
            Calls.Add((request, ownerUserId, orgId, trigger));
            if (FailWith is not null)
            {
                return Task.FromResult<(ScanCreated?, string?)>(
                    (null, FailWith));
            }
            return Task.FromResult<(ScanCreated?, string?)>(
                (new ScanCreated(Guid.NewGuid(), CoordinatorId,
                    "queued"), null));
        }

        public Task<ScanSummary?> GetAsync(
            Guid orgId, Guid scanId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ScanPage> ListAsync(Guid orgId, string? status,
            string? cursor, int limit, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ScanFindingPage> SearchFindingsAsync(Guid orgId,
            ScanFindingQuery query, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ScanFindingInfo?> GetFindingAsync(Guid orgId,
            Guid findingId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ScanFindingInfo?> UpdateFindingStatusAsync(
            Guid orgId, Guid findingId, string status,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<ScanFixResult> FixAsync(Guid orgId, Guid findingId,
            Guid actorUserId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ScanFixResult>> FixBulkAsync(
            Guid orgId, IReadOnlyList<Guid> ids, Guid actorUserId,
            CancellationToken ct) => throw new NotSupportedException();
        public Task ReconcileRemediationsAsync(CancellationToken ct) =>
            Task.CompletedTask;
    }

    private static async Task<(SqliteConnection Conn,
        Sandcastle.Infrastructure.Persistence.AppDbContext Db,
        AutomationService Svc, FakeScanService Scans,
        FakeConversationService Convs, FakeEventService Events)>
        NewAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new Sandcastle.Infrastructure.Persistence.AppDbContext(
            new Microsoft.EntityFrameworkCore
                .DbContextOptionsBuilder<
                    Sandcastle.Infrastructure.Persistence.AppDbContext>()
                .UseSqlite(conn).UseSnakeCaseNamingConvention().Options);
        await db.Database.EnsureCreatedAsync();
        var convs = new FakeConversationService();
        var events = new FakeEventService();
        var scans = new FakeScanService();
        var svc = new AutomationService(db, convs, events,
            new RecordingScanAudit(), scans: scans);
        return (conn, db, svc, scans, convs, events);
    }

    private static AutomationUpsert ScanUpsert(
        IReadOnlyList<string>? repos = null,
        string? profile = null, string? effort = null) => new(
        "nightly scan", "find security issues", null, null,
        "interval", null, 60, null, true, null,
        AutomationKinds.Scan, repos ?? ["org/repo"], profile, effort);

    [Fact]
    public async Task Create_Scan_Requires_Repositories()
    {
        var (conn, _, svc, _, _, _) = await NewAsync();
        await using var _ = conn;

        var (_, error) = await svc.CreateAsync(
            ScanUpsert(repos: []), Guid.NewGuid(), Guid.NewGuid(),
            CancellationToken.None);
        error!.ShouldContain("repositories");
    }

    [Fact]
    public async Task Create_Scan_Validates_Profile_And_Effort()
    {
        var (conn, _, svc, _, _, _) = await NewAsync();
        await using var _ = conn;

        (await svc.CreateAsync(ScanUpsert(profile: "bogus"),
            Guid.NewGuid(), null, CancellationToken.None)).Error!
            .ShouldContain("profile");
        (await svc.CreateAsync(ScanUpsert(effort: "extreme"),
            Guid.NewGuid(), null, CancellationToken.None)).Error!
            .ShouldContain("effort");
    }

    [Fact]
    public async Task Create_Scan_Persists_Config_And_Kind()
    {
        var (conn, _, svc, _, _, _) = await NewAsync();
        await using var _ = conn;

        var (info, error) = await svc.CreateAsync(
            ScanUpsert(profile: "performance", effort: "high"),
            Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        error.ShouldBeNull();
        info!.Kind.ShouldBe(AutomationKinds.Scan);
        info.ScanConfigJson.ShouldNotBeNull();
        info.ScanConfigJson.ShouldContain("performance");
        info.ScanConfigJson.ShouldContain("high");
        info.ScanConfigJson.ShouldContain("org/repo");
    }

    [Fact]
    public async Task Fire_Scan_Dispatches_IScanService_Not_Conversation()
    {
        var (conn, _, svc, scans, convs, _) = await NewAsync();
        await using var _ = conn;
        var owner = Guid.NewGuid();

        var (info, _) = await svc.CreateAsync(
            ScanUpsert(), owner, Guid.NewGuid(), CancellationToken.None);
        var (run, fired) = await svc.RunNowAsync(
            info!.Id, owner, CancellationToken.None);

        fired.ShouldBeTrue();
        // kind=scan → IScanService.CreateAsync (não conversa comum).
        scans.Calls.Count.ShouldBe(1);
        scans.Calls[0].Trigger.ShouldBe(ScanTriggers.Automation);
        scans.Calls[0].Owner.ShouldBe(owner);
        scans.Calls[0].Req.Goal.ShouldBe("find security issues");
        scans.Calls[0].Req.Repositories.ShouldBe(["org/repo"]);
        convs.Created.ShouldBe(0);
        // Run fecha succeeded — o scan tem lifecycle próprio; o
        // coordinator conversation id fica linkado no run.
        run!.Status.ShouldBe("succeeded");
        run.ConversationId.ShouldBe(scans.CoordinatorId);
    }

    [Fact]
    public async Task Task_Automation_Still_Uses_Conversation_Path()
    {
        var (conn, _, svc, scans, convs, _) = await NewAsync();
        await using var _ = conn;
        var owner = Guid.NewGuid();

        var (info, _) = await svc.CreateAsync(
            new AutomationUpsert("task", "do it", null, "org/repo",
                "interval", null, 60, null, true, null),
            owner, null, CancellationToken.None);
        var (run, fired) = await svc.RunNowAsync(
            info!.Id, owner, CancellationToken.None);

        fired.ShouldBeTrue();
        scans.Calls.ShouldBeEmpty();
        convs.Created.ShouldBe(1);
        run!.Status.ShouldBe("running"); // task runs async like before
    }

    [Fact]
    public async Task Fire_Scan_Failure_Records_Error()
    {
        var (conn, _, svc, scans, _, _) = await NewAsync();
        await using var _ = conn;
        var owner = Guid.NewGuid();
        scans.FailWith = "quota exceeded";

        var (info, _) = await svc.CreateAsync(
            ScanUpsert(), owner, null, CancellationToken.None);
        var (run, fired) = await svc.RunNowAsync(
            info!.Id, owner, CancellationToken.None);

        fired.ShouldBeTrue();
        run!.Status.ShouldBe("failed");
        run.Error.ShouldBe("quota exceeded");
    }
}