namespace Sandcastle.Domain.Conversations;

/// <summary>
/// Execution lifecycle of an app conversation (subset of upstream
/// <c>ConversationExecutionStatus</c>).
/// </summary>
public enum ConversationExecutionStatus
{
    /// <summary>Created, sandbox/agent not started.</summary>
    Stopped,

    /// <summary>Sandbox booting.</summary>
    Starting,

    /// <summary>Agent loop actively running.</summary>
    Running,

    /// <summary>Paused (user/timeout); resumable.</summary>
    Paused,

    /// <summary>Finished / no runnable work.</summary>
    Finished,

    /// <summary>Terminal failure.</summary>
    Error,

    /// <summary>Soft-deleted (deleted conversations are excluded from lists).</summary>
    Archived,
}

/// <summary>
/// An app conversation: binds a user session to a sandbox and an agent run
/// (upstream <c>AppConversationInfo</c> parity).
/// </summary>
public sealed record AppConversation(
    Guid Id,
    string? Title,
    Guid? SandboxId,
    string? SelectedRepository,
    string? AgentType,
    string? LlmModel,
    ConversationExecutionStatus ExecutionStatus,
    IReadOnlyList<string> Tags,
    bool Pinned,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? CreatedByUserId = null,
    bool ConfirmationMode = false,
    string? SandboxProfile = null,
    // SPEC-44: projeção attention (null|confirmation|question|failed).
    string? NeedsAttentionKind = null,
    // SPEC-52: PR aberto pela conversa (null = nenhum).
    string? PullRequestUrl = null,
    int? PullRequestNumber = null,
    string? PullRequestState = null,
    // SPEC-55: ticket que originou a conversa (Linear/Jira).
    string? TicketKind = null,
    string? TicketId = null,
    string? TicketUrl = null,
    // SPEC-135: conversa de origem num fork (null = raiz).
    Guid? ParentConversationId = null,
    // SPEC-146: repos da conversa, ordenados (primário ==
    // SelectedRepository); null = legado single-repo.
    IReadOnlyList<string>? Repositories = null,
    // SPEC-146: PRs por repo (aggregate); pull_request_* espelha o primário.
    IReadOnlyList<ConversationPullRequest>? PullRequests = null)
{
    /// <summary>
    /// Repos efetivos — <see cref="Repositories"/> quando preenchido;
    /// senão deriva do legado <see cref="SelectedRepository"/>.
    /// </summary>
    public IReadOnlyList<string> EffectiveRepositories =>
        Repositories is { Count: > 0 } r
            ? r
            : SelectedRepository is { Length: > 0 } s ? [s] : [];
}

/// <summary>SPEC-146: um PR criado pela conversa num repo específico.</summary>
public sealed record ConversationPullRequest(
    string Repository,
    string Url,
    int Number,
    string? State,
    string? HeadSha);