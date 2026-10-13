using OpenHandsNET.Domain.Conversations;

namespace OpenHandsNET.Application.Conversations;

/// <summary>Result of a conversation create (the async start task).</summary>
public sealed record CreateConversationRequest(
    InitialMessage? InitialMessage,
    string? SelectedRepository,
    string? AgentType,
    string? LlmModel,
    Guid? SandboxSpecId);

/// <summary>User/agent message seed for a new conversation.</summary>
public sealed record InitialMessage(string Role, IReadOnlyList<object> Content);

/// <summary>Paged search over conversations.</summary>
public sealed record ConversationQuery(
    string? TitleEq = null,
    DateTimeOffset? CreatedAtGte = null,
    string? PageId = null,
    int Limit = 100);

/// <summary>Paged result (cursor semantics equal to EventPage).</summary>
public sealed record ConversationPage(
    IReadOnlyList<AppConversation> Items, string? NextPageId);

/// <summary>Conversation aggregate service (CRUD + state).</summary>
public interface IAppConversationService
{
    /// <summary>Creates the conversation record + enqueues its start task.</summary>
    Task<AppConversationStartTask> CreateAsync(
        CreateConversationRequest request, CancellationToken ct);

    Task<ConversationPage> SearchAsync(ConversationQuery query, CancellationToken ct);

    Task<long> CountAsync(ConversationQuery query, CancellationToken ct);

    Task<IReadOnlyList<AppConversation>> GetBatchAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct);

    Task<AppConversation?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Patches title/tags/pinned.</summary>
    Task<AppConversation?> UpdateAsync(
        Guid id, string? title, IReadOnlyList<string>? tags, bool? pinned,
        CancellationToken ct);

    /// <summary>Archives the conversation and deletes its sandbox.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Validates that a user message can be sent now.
    /// Returns the upstream error key or <see langword="null"/> when OK.
    /// </summary>
    Task<SendMessageError?> ValidateSendMessageAsync(Guid id, CancellationToken ct);
}

/// <summary>Upstream-aligned send-message rejection reasons (SPEC-04 RF-001).</summary>
public enum SendMessageError
{
    /// <summary>404 — conversation does not exist.</summary>
    NotFound,

    /// <summary>410 — archived/deleted.</summary>
    Gone,

    /// <summary>409 — sandbox not running (resume first).</summary>
    ConflictSandboxNotRunning,

    /// <summary>503 — sandbox in error.</summary>
    ServiceUnavailable,
}

/// <summary>Start-task tracking + enqueue channel for the worker.</summary>
public interface IStartTaskService
{
    /// <summary>Gets one start task.</summary>
    Task<AppConversationStartTask?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Batch fetch by ids.</summary>
    Task<IReadOnlyList<AppConversationStartTask>> GetBatchAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct);

    Task<IReadOnlyList<AppConversationStartTask>> SearchAsync(
        StartTaskStatus? status, string? pageId, int limit, CancellationToken ct);

    Task<long> CountAsync(StartTaskStatus? status, CancellationToken ct);

    /// <summary>Streams task updates until terminal (READY|ERROR) — SSE feed.</summary>
    IAsyncEnumerable<AppConversationStartTask> WatchAsync(
        Guid taskId, CancellationToken ct);

    /// <summary>Enqueues a task id for the background start worker.</summary>
    ValueTask EnqueueAsync(Guid taskId, CancellationToken ct);
}
