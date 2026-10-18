namespace OpenHandsNET.Domain.Conversations;

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
    string? TicketUrl = null);
