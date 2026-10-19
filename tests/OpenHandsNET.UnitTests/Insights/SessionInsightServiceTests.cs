using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Insights;
using OpenHandsNET.Infrastructure.Persistence;

using Shouldly;

using Xunit;

namespace OpenHandsNET.UnitTests.Insights;

public class SessionInsightServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDbContext _db;
    private readonly SessionInsightService _service;

    public SessionInsightServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ohn-insights-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();

        _service = new SessionInsightService(_db, NullLogger<SessionInsightService>.Instance, null!);
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
    public async Task AnalyzeSessionAsync_Generates_Insight_For_Completed_Conversation()
    {
        var convId = Guid.NewGuid();
        var conv = new ConversationRecord
        {
            Id = convId,
            Title = "Implement feature X",
            ExecutionStatus = "COMPLETED",
            LlmModel = "claude-3-7-sonnet",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _db.Conversations.AddAsync(conv);
        await _db.SaveChangesAsync();

        var insight = await _service.AnalyzeSessionAsync(convId);

        insight.ShouldNotBeNull();
        insight.ConversationId.ShouldBe(convId);
        insight.Outcome.ShouldBe("success");
        insight.PhaseCosts.ShouldNotBeEmpty();
        insight.Learnings.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GetStatsAsync_Aggregates_Session_Metrics()
    {
        var convId = Guid.NewGuid();
        var conv = new ConversationRecord
        {
            Id = convId,
            Title = "Task with error",
            ExecutionStatus = "FAILED",
            LlmModel = "gpt-4o",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _db.Conversations.AddAsync(conv);
        await _db.SaveChangesAsync();

        await _service.AnalyzeSessionAsync(convId);
        var stats = await _service.GetStatsAsync();

        stats.TotalSessionsAnalyzed.ShouldBe(1);
        stats.FailedCount.ShouldBe(1);
        stats.SuccessRate.ShouldBe(0.0);
    }
}