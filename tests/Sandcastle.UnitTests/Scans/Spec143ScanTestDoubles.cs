using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Delegation;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Indexing;
using Sandcastle.Application.Scans;
using Sandcastle.Domain.Conversations;
using Sandcastle.Domain.Events;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Scans;

namespace Sandcastle.UnitTests.Scans;

/// <summary>
/// SPEC-143 test doubles — conversation service that records create
/// requests (agent_type / permissions / run / repo) and optionally
/// materializes <see cref="ConversationRecord"/> rows so the orchestrator
/// can poll statuses; delegation + content-provider fakes for the
/// fan-out path.
/// </summary>
internal sealed class FakeConversationService(AppDbContext? db = null)
    : IAppConversationService
{
    public int Created { get; private set; }
    public CreateConversationRequest? LastRequest { get; private set; }
    public List<CreateConversationRequest> Requests { get; } = [];
    public Guid? LastUserId { get; private set; }
    public Guid? LastOrgId { get; private set; }
    public Exception? ThrowOnCreate { get; set; }
    /// <summary>ExecutionStatus given to materialized rows.</summary>
    public string MaterializedStatus { get; set; } = "Finished";
    /// <summary>When true, inserts a ConversationRecord per create.</summary>
    public bool Materialize { get; set; } = true;

    public async Task<AppConversationStartTask> CreateForUserAsync(
        Guid userId, Guid? orgId, CreateConversationRequest request,
        CancellationToken ct)
    {
        if (ThrowOnCreate is not null)
        {
            throw ThrowOnCreate;
        }

        Created++;
        LastRequest = request;
        Requests.Add(request);
        LastUserId = userId;
        LastOrgId = orgId;
        var convId = Guid.NewGuid();
        if (Materialize && db is not null)
        {
            db.Conversations.Add(new ConversationRecord
            {
                Id = convId,
                ExecutionStatus = MaterializedStatus,
                OrgId = orgId,
                SelectedRepository = request.SelectedRepository,
                AgentType = request.AgentType,
                CreatedByUserId = userId.ToString(),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                CreatedAtTicks = DateTimeOffset.UtcNow.UtcTicks,
                UpdatedAtTicks = DateTimeOffset.UtcNow.UtcTicks,
            });
            await db.SaveChangesAsync(ct);
        }
        return new AppConversationStartTask(
            Guid.NewGuid(), StartTaskStatus.Working, convId,
            null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// <see cref="IAppConversationService.CreateAsync"/> sem user accessor:
    /// DelegationService cai neste caminho quando o pai não tem
    /// CreatedByUserId parseável — encaminha com owner sintético.
    /// </summary>
    public Task<AppConversationStartTask> CreateAsync(
        CreateConversationRequest request, CancellationToken ct) =>
        CreateForUserAsync(Guid.Empty, null, request, ct);
    public Task<ConversationPage> SearchAsync(
        ConversationQuery query, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<long> CountAsync(
        ConversationQuery query, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<AppConversation>> GetBatchAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<AppConversation?> GetAsync(
        Guid id, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<AppConversation?> UpdateAsync(Guid id, string? title,
        IReadOnlyList<string>? tags, bool? pinned,
        CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<bool> DeleteAsync(Guid id, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<SendMessageError?> ValidateSendMessageAsync(
        Guid id, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<SwitchResult> SwitchProfileAsync(
        Guid id, string? profile, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<SwitchResult> SwitchModelAsync(
        Guid id, string model, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<AppConversation?> SetPermissionsAsync(Guid id,
        IReadOnlyList<AgentPermissionRule>? rules,
        CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<AppConversation?> SetModeAsync(
        Guid id, string mode, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<AttentionItem>> ListAttentionAsync(
        int limit, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<AttentionDismissResult> DismissAttentionAsync(
        Guid id, CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>
/// Delegation fake: records requests and materializes finished child
/// conversations + a <c>scan_finding_v1</c> MessageEvent per child so the
/// orchestrator's poll + collect path runs end-to-end.
/// </summary>
internal sealed class FakeDelegation(
    AppDbContext db, string? payload = null) : IDelegationService
{
    public List<(Guid Parent, DelegateTaskRequest Req)> Calls { get; } = [];
    /// <summary>When set, DelegateAsync throws it (cap race path).</summary>
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
            ExecutionStatus = "Finished",
            SelectedRepository = request.SelectedRepository,
            AgentType = request.AgentType,
            ParentConversationId = parentConversationId,
            DelegationDepth = 1,
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
                Payload = SerializeAgentMessage(payload),
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

    /// <summary>Wraps text in the llm_message.content array shape.</summary>
    internal static string SerializeAgentMessage(string text)
    {
        var msg = new MessageEvent
        {
            Source = EventSource.Agent,
            LlmMessage = JsonSerializer.SerializeToElement(new
            {
                role = "assistant",
                content = new[] { new { type = "text", text } },
            }),
        };
        return JsonSerializer.Serialize(msg, EventJson.Options);
    }
}

/// <summary>Lease over a caller-owned temp dir (not deleted on dispose).</summary>
internal sealed class FakeRepoContentLease(string rootPath)
    : IRepoContentLease
{
    public string RootPath { get; } = rootPath;
    public string? CommitSha => "deadbeef";
    public string ProviderKind => "fake";
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Content provider that serves one prepared directory.</summary>
internal sealed class FakeRepoContentProvider(string rootPath)
    : IRepoContentProvider
{
    public List<RepoContentRequest> Requests { get; } = [];
    public Exception? ThrowOnAcquire { get; set; }

    public Task<IRepoContentLease?> AcquireAsync(
        RepoContentRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (ThrowOnAcquire is not null)
        {
            throw ThrowOnAcquire;
        }
        return Task.FromResult<IRepoContentLease?>(
            new FakeRepoContentLease(rootPath));
    }
}

/// <summary>Minimal LLM for the optional dedup pass.</summary>
internal sealed class FakeScanLlm(string script = "{}")
    : IAgentLlmProvider
{
    public string ModelId => "scan-mock";

    public async IAsyncEnumerable<ILlmStreamChunk> ChatStreamAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<ToolSchema> tools, LlmOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct)
    {
        yield return new TextDelta(script);
        yield return new CompletionEnd(
            new LlmUsage(10, 20, null), "stop", "r-1");
        await Task.CompletedTask;
    }

    public Task<ModelInfo> GetModelInfoAsync(CancellationToken ct) =>
        Task.FromResult(new ModelInfo("scan-mock", "mock"));
}

/// <summary>Router fake — always the same selection.</summary>
internal sealed class FakeScanRouter : IModelRouter
{
    public Task<ModelSelection> SelectAsync(Guid conversationId,
        ModelRole role, string? preferredModel, CancellationToken ct) =>
        Task.FromResult(new ModelSelection(
            "scan-mock", "mock", null, 0, null));

    public Task<ModelSelection?> NextFallbackAsync(
        ModelSelection current, LlmErrorKind kind, CancellationToken ct) =>
        Task.FromResult<ModelSelection?>(null);
}

/// <summary>Audit sink that records action names.</summary>
internal sealed class RecordingScanAudit : IAuditService
{
    public List<string> Actions { get; } = [];

    public Task RecordAsync(string action, string? actorUserId = null,
        Guid? orgId = null, string? targetType = null,
        string? targetId = null, string? ip = null,
        string? userAgent = null, object? metadata = null,
        CancellationToken cancellationToken = default)
    {
        Actions.Add(action);
        return Task.CompletedTask;
    }

    public Task<(IReadOnlyList<AuditEntry> Items, int Total)> QueryAsync(
        string? userId, string? action, DateTimeOffset? from,
        DateTimeOffset? to, int page, int pageSize,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<(IReadOnlyList<AuditEntry>, int)>(([], 0));
}

/// <summary>Dispatcher spy.</summary>
internal sealed class FakeScanDispatcher : IScanDispatcher
{
    public List<Guid> Enqueued { get; } = [];

    public ValueTask EnqueueAsync(Guid scanId, CancellationToken ct)
    {
        Enqueued.Add(scanId);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Shared helpers.</summary>
internal static class ScanTestDb
{
    /// <summary>SQLite in-memory AppDbContext (EnsureCreated schema).</summary>
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

    /// <summary>Seeds a scan + coordinator conversation row.</summary>
    public static async Task<ScanRecord> SeedScanAsync(
        AppDbContext db, Guid orgId, Guid coordinatorId,
        IReadOnlyList<string>? repos = null, string effort = "medium",
        string status = ScanStatuses.Queued)
    {
        db.Conversations.Add(new ConversationRecord
        {
            Id = coordinatorId,
            ExecutionStatus = "Finished",
            OrgId = orgId,
            AgentType = ScanAgentTypes.Coordinator,
            PermissionsJson = PermissionRules
                .ToJson(ScanPermissions.ReadOnly),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CreatedAtTicks = DateTimeOffset.UtcNow.UtcTicks,
            UpdatedAtTicks = DateTimeOffset.UtcNow.UtcTicks,
        });
        var scan = new ScanRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            CreatedByUserId = Guid.NewGuid().ToString(),
            RepositoriesJson = JsonSerializer.Serialize(
                repos ?? ["org/repo"]),
            Goal = "find issues",
            Profile = ScanProfiles.Security,
            Effort = effort,
            Status = status,
            Trigger = ScanTriggers.Manual,
            CoordinatorConversationId = coordinatorId,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedAtTicks = DateTimeOffset.UtcNow.UtcTicks,
        };
        db.Scans.Add(scan);
        await db.SaveChangesAsync();
        return scan;
    }
}