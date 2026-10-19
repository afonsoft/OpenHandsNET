using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Artifacts;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Delegation;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Domain.Events;
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
    private readonly FakeConversationService _convs;
    private readonly DelegationService _service;

    public DelegationServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ohn-delegation-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();

        _convs = new FakeConversationService(_db);
        _service = new DelegationService(
            _db, _convs, NullLogger<DelegationService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private async Task<ConversationRecord> SeedParentAsync(int depth = 0)
    {
        var parent = new ConversationRecord
        {
            Id = Guid.NewGuid(),
            Title = "Coordinator",
            ExecutionStatus = ConversationExecutionStatus.Running.ToString(),
            DelegationDepth = depth,
            Mode = "plan",
            ConfirmationMode = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _db.Conversations.AddAsync(parent);
        await _db.SaveChangesAsync();
        return parent;
    }

    [Fact]
    public async Task DelegateAsync_Creates_Child_Via_StartTask_Pipeline()
    {
        var parent = await SeedParentAsync();

        var res = await _service.DelegateAsync(parent.Id, new DelegateTaskRequest("Subtask 1"));

        res.ChildConversationId.ShouldBe(_convs.ConversationId);
        res.DelegationDepth.ShouldBe(1);
        res.Status.ShouldBe(StartTaskStatus.Working.ToString());

        // Filha real, persistida e ligada ao pai (start-task pipeline).
        var child = await _db.Conversations.FindAsync(res.ChildConversationId);
        child.ShouldNotBeNull();
        child!.ParentConversationId.ShouldBe(parent.Id);
        child.DelegationDepth.ShouldBe(1);
        child.ExecutionStatus.ShouldBe(
            ConversationExecutionStatus.Starting.ToString());

        // Restrições do pai propagadas no request de criação.
        _convs.LastRequest.ShouldNotBeNull();
        _convs.LastRequest!.Mode.ShouldBe("plan");
        _convs.LastRequest.ConfirmationMode.ShouldBeTrue();
        _convs.LastRequest.InitialMessage!.Run.ShouldBeTrue();

        var children = await _service.ListSiblingsAsync(parent.Id);
        children.Count.ShouldBe(1);
        children[0].ConversationId.ShouldBe(res.ChildConversationId);
        children[0].ParentConversationId.ShouldBe(parent.Id);
    }

    [Fact]
    public async Task DelegateAsync_Enforces_Max_Depth()
    {
        var parent = await SeedParentAsync(depth: 2);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await _service.DelegateAsync(parent.Id, new DelegateTaskRequest("Subtask nested"));
        });
    }

    /// <summary>
    /// Fake mínimo do pipeline: persiste a conversa como CreateCoreAsync faz
    /// (STARTING) e devolve a start-task Working apontando para ela.
    /// </summary>
    private sealed class FakeConversationService(AppDbContext db)
        : IAppConversationService
    {
        public Guid ConversationId { get; } = Guid.NewGuid();
        public CreateConversationRequest? LastRequest { get; private set; }

        public async Task<AppConversationStartTask> CreateAsync(
            CreateConversationRequest request, CancellationToken ct)
        {
            LastRequest = request;
            var now = DateTimeOffset.UtcNow;
            db.Conversations.Add(new ConversationRecord
            {
                Id = ConversationId,
                Title = "delegated",
                ExecutionStatus = ConversationExecutionStatus.Starting.ToString(),
                Mode = request.Mode ?? "build",
                ConfirmationMode = request.ConfirmationMode,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync(ct);
            return new AppConversationStartTask(
                Guid.NewGuid(), StartTaskStatus.Working, ConversationId,
                null, null, now, now);
        }

        public Task<AppConversationStartTask> CreateForUserAsync(
            Guid userId, Guid? orgId, CreateConversationRequest request,
            CancellationToken ct) => CreateAsync(request, ct);

        public Task<ConversationPage> SearchAsync(ConversationQuery q, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> CountAsync(ConversationQuery q, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AppConversation>> GetBatchAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => throw new NotSupportedException();
        public Task<AppConversation?> GetAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<AppConversation?> UpdateAsync(Guid id, string? title, IReadOnlyList<string>? tags, bool? pinned, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<SendMessageError?> ValidateSendMessageAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<SwitchResult> SwitchProfileAsync(Guid id, string? profile, CancellationToken ct) => throw new NotSupportedException();
        public Task<SwitchResult> SwitchModelAsync(Guid id, string model, CancellationToken ct) => throw new NotSupportedException();
        public Task<AppConversation?> SetPermissionsAsync(Guid id, IReadOnlyList<AgentPermissionRule>? rules, CancellationToken ct) => throw new NotSupportedException();
        public Task<AppConversation?> SetModeAsync(Guid id, string mode, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AttentionItem>> ListAttentionAsync(int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<AttentionDismissResult> DismissAttentionAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }
}