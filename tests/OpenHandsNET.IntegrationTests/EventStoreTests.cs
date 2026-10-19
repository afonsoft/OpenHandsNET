using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Eventing;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.IntegrationTests;

/// <summary>
/// Event store integration tests against a real SQLite database
/// (SPEC-02 RF-002/RF-003, AC-02/AC-04).
/// </summary>
public sealed class EventStoreTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"openhandsnet-events-{Guid.NewGuid():N}.db");
    private AppDbContext _db = null!;
    private EventStore _store = null!;
    private readonly Guid _conversationId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .UseSnakeCaseNamingConvention()
            .Options;
        _db = new AppDbContext(options);
        await _db.Database.MigrateAsync();
        _store = new EventStore(_db, new InMemoryEventBus());
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        return Task.CompletedTask;
    }

    private MessageEvent Msg(string text, DateTimeOffset? ts = null) => new()
    {
        Source = EventSource.User,
        Timestamp = ts ?? DateTimeOffset.UtcNow,
        LlmMessage = System.Text.Json.JsonSerializer.SerializeToElement(
            new { role = "user", content = new[] { new { type = "text", text } } }),
    };

    [Fact]
    public async Task Append_And_Count()
    {
        await _store.AppendAsync(_conversationId, Msg("a"), CancellationToken.None);
        await _store.AppendAsync(_conversationId, Msg("b"), CancellationToken.None);
        (await _store.CountAsync(_conversationId, new EventQuery(), CancellationToken.None))
            .ShouldBe(2);
        (await _store.CountAsync(
            _conversationId, new EventQuery(KindEq: "MessageEvent"), CancellationToken.None))
            .ShouldBe(2);
        (await _store.CountAsync(
            _conversationId, new EventQuery(KindEq: "PauseEvent"), CancellationToken.None))
            .ShouldBe(0);
    }

    // Covers AC-02: timestamp range + kind filters honored.
    [Fact]
    public async Task Search_Filters_Kind_And_Timestamp_Range()
    {
        var t0 = DateTimeOffset.UtcNow;
        await _store.AppendAsync(_conversationId, Msg("old", t0), CancellationToken.None);
        await _store.AppendAsync(
            _conversationId, new PauseEvent { Timestamp = t0.AddSeconds(5) }, CancellationToken.None);
        await _store.AppendAsync(_conversationId, Msg("new", t0.AddSeconds(10)), CancellationToken.None);

        var page = await _store.SearchAsync(_conversationId, new EventQuery(
            KindEq: "MessageEvent",
            TimestampGte: t0.AddSeconds(1),
            TimestampLt: t0.AddSeconds(20)), CancellationToken.None);

        page.Items.ShouldHaveSingleItem();
        page.NextPageId.ShouldBeNull();
    }

    // Covers AC-02: cursored paging must not duplicate items across pages.
    [Fact]
    public async Task Search_Pages_Without_Duplicates()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 25; i++)
        {
            await _store.AppendAsync(
                _conversationId, Msg($"m{i}", t0.AddSeconds(i)), CancellationToken.None);
        }

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await _store.SearchAsync(
                _conversationId, new EventQuery(PageId: cursor, Limit: 10), CancellationToken.None);
            seen.AddRange(page.Items.Select(e => e.Id));
            cursor = page.NextPageId;
            pages++;
        }
        while (cursor is not null);

        pages.ShouldBe(3);
        seen.Count.ShouldBe(25);
        seen.Distinct().Count().ShouldBe(25);
    }

    [Fact]
    public async Task Search_Descending_Order()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        await _store.AppendAsync(_conversationId, Msg("first", t0), CancellationToken.None);
        await _store.AppendAsync(_conversationId, Msg("second", t0.AddSeconds(1)), CancellationToken.None);

        var page = await _store.SearchAsync(_conversationId,
            new EventQuery(Sort: EventSortOrder.TimestampDesc), CancellationToken.None);
        page.Items.Select(e => e.Timestamp).ShouldBe([t0.AddSeconds(1), t0]);
    }

    [Fact]
    public async Task GetBatch_Preserves_Id_Order()
    {
        var a = await _store.AppendAsync(_conversationId, Msg("a"), CancellationToken.None);
        var b = await _store.AppendAsync(_conversationId, Msg("b"), CancellationToken.None);

        var batch = await _store.GetBatchAsync(
            _conversationId, [b.Id, a.Id], CancellationToken.None);
        batch.Select(e => e.Id).ShouldBe([b.Id, a.Id]);
    }

    // Covers AC-04: subscribe delivers appended events in order, live.
    [Fact]
    public async Task Subscribe_Delivers_Appends_In_Order()
    {
        var received = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = Task.Run(async () =>
        {
            await foreach (var e in _store.SubscribeAsync(_conversationId, cts.Token))
            {
                received.Add(e.Id);
                if (received.Count == 3)
                {
                    break;
                }
            }
        });

        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var e = await _store.AppendAsync(_conversationId, Msg($"s{i}"), CancellationToken.None);
            ids.Add(e.Id);
        }

        await reader;
        received.ShouldBe(ids);
    }

    [Fact]
    public async Task Events_Are_Immutable_And_AppendOnly()
    {
        var e = await _store.AppendAsync(_conversationId, Msg("x"), CancellationToken.None);
        var loaded = await _db.Events.FindAsync(e.Id);
        loaded.ShouldNotBeNull();
        loaded.Payload.ShouldContain("\"kind\":\"MessageEvent\"");
        // Sem UPDATE/DELETE exposto: re-append do mesmo id deve falhar (PK).
        await using var db2 = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .UseSnakeCaseNamingConvention().Options);
        db2.Events.Add(new EventRecord
        {
            Id = e.Id,
            ConversationId = _conversationId,
            Kind = e.EventKind,
            Source = "user",
            TimestampTicks = e.Timestamp.UtcTicks,
            Payload = "{}",
        });
        await Should.ThrowAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }
}

// Extensão do arquivo: benchmark SPEC-02 (10k eventos, página < 200 ms alvo).
public sealed class EventStorePerfTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"openhandsnet-perf-{Guid.NewGuid():N}.db");
    private AppDbContext _db = null!;
    private EventStore _store = null!;
    private readonly Guid _conversationId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .UseSnakeCaseNamingConvention().Options);
        await _db.Database.MigrateAsync();
        _store = new EventStore(_db, new InMemoryEventBus());
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    // SPEC-02 DoD: 10_000 eventos/conversa, página < 200 ms (margem de segurança no assert).
    [Fact(Timeout = 120_000)]
    public async Task Search_Page_Under_Time_Budget_At_10k_Events()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 10_000; i++)
        {
            await _store.AppendAsync(_conversationId, new MessageEvent
            {
                Source = EventSource.User,
                Timestamp = t0.AddMilliseconds(i),
                LlmMessage = System.Text.Json.JsonSerializer.SerializeToElement(
                    new { role = "user", content = new[] { new { type = "text", text = $"m{i}" } } }),
            }, CancellationToken.None);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var page = await _store.SearchAsync(
            _conversationId, new EventQuery(Limit: 100), CancellationToken.None);
        sw.Stop();
        page.Items.Count.ShouldBe(100);
        page.NextPageId.ShouldNotBeNull();
        // Alvo 200 ms; assert com folga p/ CI (medida real reportada no PR).
        sw.ElapsedMilliseconds.ShouldBeLessThan(2_000);
    }
}