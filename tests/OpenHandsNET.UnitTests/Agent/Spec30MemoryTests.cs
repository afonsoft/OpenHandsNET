using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Discovery;
using OpenHandsNET.Application.Memory;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Agent.Tools;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Discovery;
using OpenHandsNET.Infrastructure.Memory;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Infrastructure.Skills;
using Shouldly;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-30 AC: memory tool (project files + org DB), digest no prompt,
/// soft-delete + audit, endpoints de memória.
/// </summary>
public sealed class Spec30MemoryTests
{
    private static readonly Domain.Sandbox.Sandbox TestSandbox = new(
        Guid.NewGuid(), Guid.Empty, null, "fake", SandboxStatus.Running,
        null, DateTimeOffset.UtcNow, null, null);

    private static ToolCallContext Ctx(
        FakeSandboxExecutor exec, Guid? conversationId = null,
        IServiceProvider? services = null) => new(
        conversationId ?? Guid.NewGuid(), TestSandbox, exec,
        new FakeEventService(),
        services ?? new ServiceCollection().BuildServiceProvider(),
        TimeSpan.FromSeconds(30));

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<(ServiceProvider sp, SqliteConnection conn,
        Guid orgId, Guid convId)> OrgScopeAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var sc = new ServiceCollection();
        sc.AddDbContext<AppDbContext>(o =>
            o.UseSqlite(conn).UseSnakeCaseNamingConvention());
        sc.AddScoped<IMemoryService>(sp => new MemoryService(
            sp.GetRequiredService<AppDbContext>(), Audit));
        var sp = sc.BuildServiceProvider();
        var db = sp.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        var orgId = Guid.NewGuid();
        var convId = Guid.NewGuid();
        db.Conversations.Add(new ConversationRecord
        {
            Id = convId, OrgId = orgId, ExecutionStatus = "Running",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (sp, conn, orgId, convId);
    }

    private static readonly Application.Identity.IAuditService Audit =
        new RecordingAudit();

    private sealed class RecordingAudit
        : Application.Identity.IAuditService
    {
        public static List<string> Actions { get; } = [];

        public Task RecordAsync(string action, string? actorUserId = null,
            Guid? orgId = null, string? targetType = null,
            string? targetId = null, string? ip = null,
            string? userAgent = null, object? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }

        public Task<(IReadOnlyList<Application.Identity.AuditEntry> Items,
            int Total)> QueryAsync(string? userId, string? action,
            DateTimeOffset? from, DateTimeOffset? to, int page, int pageSize,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<Application.Identity.AuditEntry> empty = [];
            return Task.FromResult<(IReadOnlyList<
                Application.Identity.AuditEntry> Items, int Total)>(
                    (empty, 0));
        }
    }

    // ---- project scope ----------------------------------------------------

    [Fact]
    public async Task Remember_Project_Writes_File_And_Index()
    {
        var exec = new FakeSandboxExecutor();
        var res = await BuiltinTools.Memory().Handler(Ctx(exec),
            Args("""{"op":"remember","scope":"project","key":"coding-style","text":"tabs not spaces"}"""),
            CancellationToken.None);
        res.IsError.ShouldBeFalse();

        (await exec.ReadFileAsync(TestSandbox,
            "/workspace/repo/.openhands/memory/coding-style.md",
            CancellationToken.None)).ShouldContain("tabs not spaces");
        (await exec.ReadFileAsync(TestSandbox,
            "/workspace/repo/.openhands/memory/INDEX.md",
            CancellationToken.None)).ShouldContain("- coding-style");
    }

    [Fact]
    public async Task Recall_And_Forget_Project_Round_Trip()
    {
        var exec = new FakeSandboxExecutor();
        var ctx = Ctx(exec);
        await BuiltinTools.Memory().Handler(ctx,
            Args("""{"op":"remember","scope":"project","key":"k1","text":"v1"}"""),
            CancellationToken.None);

        var rec = await BuiltinTools.Memory().Handler(ctx,
            Args("""{"op":"recall","scope":"project","key":"k1"}"""),
            CancellationToken.None);
        rec.Observation.GetProperty("entries")[0]
            .GetProperty("text").GetString()!.ShouldContain("v1");

        await BuiltinTools.Memory().Handler(ctx,
            Args("""{"op":"forget","scope":"project","key":"k1"}"""),
            CancellationToken.None);
        var gone = await BuiltinTools.Memory().Handler(ctx,
            Args("""{"op":"recall","scope":"project","key":"k1"}"""),
            CancellationToken.None);
        gone.IsError.ShouldBeTrue();
    }

    // ---- org scope --------------------------------------------------------

    [Fact]
    public async Task Remember_Org_Persists_And_Audits()
    {
        var (sp, conn, orgId, convId) = await OrgScopeAsync();
        await using var _ = conn; await using var __ = sp;
        var exec = new FakeSandboxExecutor();
        var res = await BuiltinTools.Memory().Handler(
            Ctx(exec, convId, sp),
            Args("""{"op":"remember","scope":"org","key":"deploy-day","text":"friday"}"""),
            CancellationToken.None);
        res.IsError.ShouldBeFalse();

        var db = sp.GetRequiredService<AppDbContext>();
        var row = await db.MemoryRecords.SingleAsync();
        row.OrgId.ShouldBe(orgId);
        row.Key.ShouldBe("deploy-day");
        RecordingAudit.Actions.ShouldContain("memory_remember");
    }

    [Fact]
    public async Task Promote_Copies_Project_Memory_To_Org()
    {
        var (sp, conn, orgId, convId) = await OrgScopeAsync();
        await using var _ = conn; await using var __ = sp;
        var exec = new FakeSandboxExecutor();
        var ctx = Ctx(exec, convId, sp);
        await BuiltinTools.Memory().Handler(ctx,
            Args("""{"op":"remember","scope":"project","key":"convention","text":"use records"}"""),
            CancellationToken.None);

        var res = await BuiltinTools.Memory().Handler(ctx,
            Args("""{"op":"promote","key":"convention"}"""),
            CancellationToken.None);
        res.IsError.ShouldBeFalse();
        (await sp.GetRequiredService<AppDbContext>().MemoryRecords
            .SingleAsync()).Key.ShouldBe("convention");
    }

    // ---- digest -----------------------------------------------------------

    [Fact]
    public async Task Prompt_Contains_Memory_Digest()
    {
        var exec = new FakeSandboxExecutor();
        exec.SeedFile("/workspace/repo/.openhands/memory/INDEX.md",
            "- coding-style — tabs\n");
        var (sp, conn, orgId, convId) = await OrgScopeAsync();
        await using var _ = conn; await using var __ = sp;
        var memory = new MemoryService(
            sp.GetRequiredService<AppDbContext>(), null);
        await memory.RememberOrgAsync(orgId, "tz", "use UTC", "u1",
            CancellationToken.None);

        var db = sp.GetRequiredService<AppDbContext>();
        var conv = (await db.Conversations.FindAsync(convId))!;
        conv.SandboxId = TestSandbox.Id;
        await db.SaveChangesAsync();

        var loader = new RepoContextLoader(
            new RepoDiscoveryService(NullLogger<RepoDiscoveryService>.Instance),
            new RepoSkillProvider(NullLogger<RepoSkillProvider>.Instance),
            new FakeSandboxService(TestSandbox.Id), exec,
            NullLogger<RepoContextLoader>.Instance, memory);
        var loaded = await loader.LoadAsync(conv, [], CancellationToken.None);

        loaded.RenderedMarkdown.ShouldNotBeNull();
        loaded.RenderedMarkdown.ShouldContain("## Memory");
        loaded.RenderedMarkdown.ShouldContain("<memory scope=\"project\">");
        loaded.RenderedMarkdown.ShouldContain("coding-style");
        loaded.RenderedMarkdown.ShouldContain("<memory scope=\"org\">");
        loaded.RenderedMarkdown.ShouldContain("use UTC");
    }
}
