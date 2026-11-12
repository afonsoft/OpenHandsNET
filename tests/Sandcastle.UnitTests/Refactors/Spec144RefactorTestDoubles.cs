using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Delegation;
using Sandcastle.Application.Refactors;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.UnitTests.Scans;

namespace Sandcastle.UnitTests.Refactors;

/// <summary>
/// SPEC-144 test doubles + helpers — SQLite in-memory AppDbContext, a
/// recording <see cref="IRefactorDispatcher"/> and a delegation fake that
/// materializes batch children with PR fields + an optional
/// <c>refactor_batch_v1</c> output event so the executor's collect path
/// runs end-to-end.
/// </summary>
internal static class RefactorTestDb
{
    public static async Task<(SqliteConnection Conn, AppDbContext Db)>
        NewAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(conn).UseSnakeCaseNamingConvention().Options);
        await db.Database.EnsureCreatedAsync();
        return (conn, db);
    }
}

/// <summary>Recording refactor dispatcher — captures (plan, kind) pairs.</summary>
internal sealed class FakeRefactorDispatcher : IRefactorDispatcher
{
    public List<(Guid PlanId, RefactorWorkKind Kind)> Enqueued { get; } = [];

    public ValueTask EnqueueAsync(Guid planId, RefactorWorkKind kind,
        CancellationToken ct)
    {
        Enqueued.Add((planId, kind));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Recording runner host — NotifyMessageAsync calls only.</summary>
internal sealed class FakeRunnerHost : IAgentRunnerHost
{
    public List<Guid> Notified { get; } = [];

    public Task NotifyMessageAsync(Guid conversationId,
        CancellationToken ct)
    {
        Notified.Add(conversationId);
        return Task.CompletedTask;
    }

    public Task ConfirmActionAsync(Guid conversationId, string actionId,
        bool approved, string? reason, CancellationToken ct) =>
        Task.CompletedTask;
    public Task InterruptAsync(Guid conversationId,
        CancellationToken ct) => Task.CompletedTask;
    public Task PauseAsync(Guid conversationId,
        CancellationToken ct) => Task.CompletedTask;
    public Task ResumeAsync(Guid conversationId,
        CancellationToken ct) => Task.CompletedTask;
    public bool IsRunning(Guid conversationId) => false;
}

/// <summary>
/// Delegation fake for refactor batches — materializes the child
/// conversation with a configurable execution status (default Finished)
/// plus optional PR fields and a <c>refactor_batch_v1</c> agent message
/// (the executor reads both on the collect path).
/// </summary>
internal sealed class FakeRefactorDelegation(
    AppDbContext db,
    string? payload = null,
    string materializedStatus = "Finished",
    string? prUrl = "https://github.com/org/repo/pull/42",
    int? prNumber = 42) : IDelegationService
{
    public List<(Guid Parent, DelegateTaskRequest Req)> Calls { get; } = [];
    public Exception? ThrowOnce { get; set; }

    public async Task<DelegateTaskResponse> DelegateAsync(
        Guid parentConversationId, DelegateTaskRequest request,
        CancellationToken ct = default)
    {
        Calls.Add((parentConversationId, request));
        if (ThrowOnce is { } ex)
        {
            ThrowOnce = null;
            throw ex;
        }

        var childId = Guid.NewGuid();
        db.Conversations.Add(new ConversationRecord
        {
            Id = childId,
            ExecutionStatus = materializedStatus,
            SelectedRepository = request.SelectedRepository,
            AgentType = request.AgentType,
            ParentConversationId = parentConversationId,
            DelegationDepth = 1,
            PullRequestUrl = prUrl,
            PullRequestNumber = prNumber,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CreatedAtTicks = DateTimeOffset.UtcNow.UtcTicks,
            UpdatedAtTicks = DateTimeOffset.UtcNow.UtcTicks,
        });

        if (payload is not null)
        {
            db.Events.Add(new Sandcastle.Infrastructure.Eventing
                .EventRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = childId,
                Kind = "MessageEvent",
                Source = "agent",
                TimestampTicks = DateTimeOffset.UtcNow.UtcTicks,
                Payload = FakeDelegation.SerializeAgentMessage(payload),
            });
        }
        await db.SaveChangesAsync(ct);
        return new DelegateTaskResponse(childId, 1, "Working");
    }

    public Task<IReadOnlyList<DelegationStatusDto>> ListSiblingsAsync(
        Guid conversationId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task WakeParentOnCompletionAsync(Guid childConversationId,
        string outcome, string summary, CancellationToken ct = default) =>
        Task.CompletedTask;
}