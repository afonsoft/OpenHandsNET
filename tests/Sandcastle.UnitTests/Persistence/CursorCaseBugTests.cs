using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Conversations;
using Sandcastle.Domain.Conversations;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Sandboxing;
using Sandcastle.UnitTests.Agent;
using Sandcastle.UnitTests.Refactors;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Persistence;

/// <summary>
/// #424 — cursor pagination over Guid ids: SQLite persists Guid as TEXT
/// uppercase while <see cref="CursorPaginator.Encode"/> writes
/// <c>Guid.ToString()</c> lowercase; without normalizing both sides the
/// (ticks,id) tie-break repeats the cursor row on the next page.
/// Covers <see cref="AppConversationService"/> +
/// <see cref="StartTaskService"/>; the SCM run services are covered in
/// their spec test files.
/// </summary>
public sealed class CursorCaseBugTests
{
    private static AppDbContext NewDb(SqliteConnection conn)
    {
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(conn).UseSnakeCaseNamingConvention().Options);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task Conversation_search_paginates_guid_boundary()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = NewDb(conn);

        // CreatedAt idêntico → toda quebra de página cai no tie-break
        // por id (Guid TEXT uppercase no SQLite vs cursor lowercase).
        var at = DateTimeOffset.UtcNow;
        var seeded = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var row = new ConversationRecord
            {
                Id = Guid.NewGuid(),
                ExecutionStatus = "Finished",
                Title = $"conv-{i}",
                CreatedAt = at,
                UpdatedAt = at,
            };
            db.Conversations.Add(row);
            seeded.Add(row.Id);
        }
        await db.SaveChangesAsync();

        var workspace = Path.Combine(
            Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var svc = new AppConversationService(
            db, new NullStartTasks(),
            new FakeSandboxService(Guid.NewGuid()),
            new FakeEventService(),
            new WorkspaceProvisioner(
                Options.Create(new SandboxOptions
                {
                    WorkspaceRoot = workspace,
                }),
                NullLogger<WorkspaceProvisioner>.Instance),
            new FakeRunnerHost());

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await svc.SearchAsync(
                new ConversationQuery(PageId: cursor, Limit: 2),
                CancellationToken.None);
            seen.AddRange(page.Items.Select(c => c.Id));
            cursor = page.NextPageId;
        }
        while (cursor is not null);

        seen.ShouldBe(seeded, ignoreOrder: true);
    }

    [Fact]
    public async Task Start_task_search_paginates_guid_boundary()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var sc = new ServiceCollection();
        sc.AddDbContext<AppDbContext>(o => o.UseSqlite(conn)
            .UseSnakeCaseNamingConvention());
        await using var sp = sc.BuildServiceProvider();
        const int total = 5;
        await using (var db = sp.GetRequiredService<AppDbContext>())
        {
            await db.Database.EnsureCreatedAsync();
            var at = DateTimeOffset.UtcNow;
            for (var i = 0; i < total; i++)
            {
                db.StartTasks.Add(new StartTaskRecord
                {
                    Id = Guid.NewGuid(),
                    Status = nameof(StartTaskStatus.Ready),
                    CreatedAt = at,
                    UpdatedAt = at,
                });
            }
            await db.SaveChangesAsync();
        }

        var svc = new StartTaskService(
            sp.GetRequiredService<IServiceScopeFactory>());

        // O service devolve só a página — o cursor é montado pelo caller
        // a partir da última linha (mesmo codec do cursor HTTP).
        var seen = new List<Guid>();
        string? cursor = null;
        while (true)
        {
            var page = await svc.SearchAsync(
                null, cursor, 2, CancellationToken.None);
            seen.AddRange(page.Select(r => r.Id));
            if (page.Count < 2)
            {
                break;
            }
            var last = page[^1];
            cursor = CursorPaginator.Encode(
                last.CreatedAt.UtcTicks, last.Id.ToString());
        }

        seen.Count.ShouldBe(total);
        seen.Distinct().Count().ShouldBe(total);
    }

    private sealed class NullStartTasks : IStartTaskService
    {
        public Task<AppConversationStartTask?> GetAsync(
            Guid id, CancellationToken ct) =>
            Task.FromResult<AppConversationStartTask?>(null);

        public Task<IReadOnlyList<AppConversationStartTask>> GetBatchAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AppConversationStartTask>>([]);

        public Task<IReadOnlyList<AppConversationStartTask>> SearchAsync(
            StartTaskStatus? status, string? pageId, int limit,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AppConversationStartTask>>([]);

        public Task<long> CountAsync(
            StartTaskStatus? status, CancellationToken ct) =>
            Task.FromResult(0L);

        public async IAsyncEnumerable<AppConversationStartTask> WatchAsync(
            Guid taskId,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask EnqueueAsync(Guid taskId, CancellationToken ct) =>
            ValueTask.CompletedTask;
    }
}