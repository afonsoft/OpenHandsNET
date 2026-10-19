using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenHandsNET.Application.Delegation;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Delegation;
using OpenHandsNET.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace OpenHandsNET.UnitTests.Delegation;

public class DelegationServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDbContext _db;
    private readonly DelegationService _service;

    public DelegationServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ohn-delegation-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();

        _service = new DelegationService(_db, NullLogger<DelegationService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task DelegateAsync_Creates_Child_Conversation_With_Correct_Depth()
    {
        var parentId = Guid.NewGuid();
        var parent = new ConversationRecord
        {
            Id = parentId,
            Title = "Coordinator",
            ExecutionStatus = "READY",
            DelegationDepth = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _db.Conversations.AddAsync(parent);
        await _db.SaveChangesAsync();

        var req = new DelegateTaskRequest(TaskDescription = "Subtask 1");
        var res = await _service.DelegateAsync(parentId, req);

        res.ShouldNotBeNull();
        res.DelegationDepth.ShouldBe(1);
        res.Status.ShouldBe("READY");

        var children = await _service.ListSiblingsAsync(parentId);
        children.Count.ShouldBe(1);
        children[0].ConversationId.ShouldBe(res.ChildConversationId);
        children[0].ParentConversationId.ShouldBe(parentId);
    }

    [Fact]
    public async Task DelegateAsync_Enforces_Max_Depth()
    {
        var parentId = Guid.NewGuid();
        var parent = new ConversationRecord
        {
            Id = parentId,
            Title = "Coordinator Max Depth",
            ExecutionStatus = "READY",
            DelegationDepth = 2, // Max allowed depth is 2
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _db.Conversations.AddAsync(parent);
        await _db.SaveChangesAsync();

        var req = new DelegateTaskRequest(TaskDescription = "Subtask nested");
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await _service.DelegateAsync(parentId, req);
        });
    }
}
