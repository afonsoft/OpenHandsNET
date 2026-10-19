using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OpenHandsNET.Application.Knowledge;
using OpenHandsNET.Application.Memory;
using OpenHandsNET.Infrastructure.Knowledge;
using OpenHandsNET.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace OpenHandsNET.UnitTests.Knowledge;

/// <summary>SPEC-63: fila de sugestões + groom dedup/conflict/stale.</summary>
public sealed class Spec63KnowledgeTests : IDisposable
{
    private readonly SqliteConnection _conn = new(
        "Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly KnowledgeService _svc;
    private readonly FakeMemory _memory = new();

    public Spec63KnowledgeTests()
    {
        _conn.Open();
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
        _svc = new KnowledgeService(_db, _memory,
            new ConfigurationBuilder().Build());
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task Suggest_CreatesPendingEntry()
    {
        var s = await _svc.SuggestAsync(
            "cache TTL", "use 60s", "org", null,
            SuggestionKind.Entry, "manual", null, null, default);
        s.Status.ShouldBe(SuggestionStatus.Pending);
        (await _svc.ListSuggestionsAsync(
            SuggestionStatus.Pending, null, default))
            .ShouldContain(x => x.Id == s.Id);
    }

    [Fact]
    public async Task Approve_WritesMemoryAndResolves()
    {
        var s = await _svc.SuggestAsync(
            "Cache TTL", "use 60s", "org", null,
            SuggestionKind.Entry, null, null, null, default);
        await _svc.ApproveAsync(s.Id, null, "u1", default);
        _memory.Writes.ShouldContain(
            w => w.Key == "cache-ttl" && w.Text == "use 60s");
        var after = await _svc.ListSuggestionsAsync(
            SuggestionStatus.Approved, null, default);
        after.ShouldContain(x => x.Id == s.Id);
    }

    [Fact]
    public async Task Approve_WithOverride_UsesOverrideText()
    {
        var s = await _svc.SuggestAsync(
            "t", "original", "org", null,
            SuggestionKind.Entry, null, null, null, default);
        await _svc.ApproveAsync(s.Id, "edited", null, default);
        _memory.Writes.ShouldContain(w => w.Text == "edited");
    }

    [Fact]
    public async Task Dismiss_ClosesSuggestion()
    {
        var s = await _svc.SuggestAsync(
            "t", "c", "org", null,
            SuggestionKind.Entry, null, null, null, default);
        await _svc.DismissAsync(s.Id, "not useful", default);
        (await _svc.ListSuggestionsAsync(
            SuggestionStatus.Dismissed, null, default))
            .ShouldContain(x => x.Id == s.Id);
    }

    [Fact]
    public async Task ApproveTwice_ThrowsResolved()
    {
        var s = await _svc.SuggestAsync(
            "t", "c", "org", null,
            SuggestionKind.Entry, null, null, null, default);
        await _svc.ApproveAsync(s.Id, null, null, default);
        await Should.ThrowAsync<SuggestionResolvedException>(
            () => _svc.ApproveAsync(s.Id, null, null, default));
    }

    [Fact]
    public async Task Groom_FlagsDuplicatePair()
    {
        _db.MemoryRecords.AddRange(
            new MemoryRecord
            {
                Id = Guid.NewGuid(), OrgId = Guid.Empty,
                Key = "a",
                Text = "use postgres for production data store",
            },
            new MemoryRecord
            {
                Id = Guid.NewGuid(), OrgId = Guid.Empty,
                Key = "b",
                Text = "use postgres for production data store",
            });
        await _db.SaveChangesAsync();
        var report = await _svc.GroomAsync(default);
        report.Duplicates.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Groom_FlagsStaleEntries()
    {
        _db.MemoryRecords.Add(new MemoryRecord
        {
            Id = Guid.NewGuid(), OrgId = Guid.Empty, Key = "old",
            Text = "ancient note",
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-120),
        });
        await _db.SaveChangesAsync();
        var report = await _svc.GroomAsync(default);
        report.Stale.ShouldNotBeEmpty();
    }
}

internal sealed class FakeMemory : IMemoryService
{
    public List<(string Key, string Text)> Writes { get; } = [];

    public Task<MemoryEntry> RememberOrgAsync(Guid orgId, string key,
        string text, string? actorUserId, CancellationToken ct)
    {
        Writes.Add((key, text));
        return Task.FromResult(new MemoryEntry(
            key, text, "org", DateTimeOffset.UtcNow));
    }

    public Task<IReadOnlyList<MemoryEntry>> ListOrgAsync(
        Guid orgId, string? query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

    public Task<bool> ForgetOrgAsync(Guid orgId, string key,
        string? actorUserId, CancellationToken ct) =>
        Task.FromResult(true);

    public Task<IReadOnlyList<MemoryEntry>> DigestAsync(
        Guid orgId, int maxEntries, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
}
