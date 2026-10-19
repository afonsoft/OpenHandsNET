namespace OpenHandsNET.Application.Delegation;

public sealed record DelegateTaskRequest(
    string TaskDescription,
    string? SelectedRepository = null,
    string? AgentType = null,
    string? LlmModel = null);

public sealed record DelegateTaskResponse(
    Guid ChildConversationId,
    int DelegationDepth,
    string Status);

public sealed record DelegationStatusDto(
    Guid ConversationId,
    Guid? ParentConversationId,
    int DelegationDepth,
    string ExecutionStatus,
    string Title,
    DateTimeOffset CreatedAt);

public interface IDelegationService
{
    Task<DelegateTaskResponse> DelegateAsync(Guid parentConversationId, DelegateTaskRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<DelegationStatusDto>> ListSiblingsAsync(Guid conversationId, CancellationToken ct = default);
    Task WakeParentOnCompletionAsync(Guid childConversationId, string outcome, string summary, CancellationToken ct = default);
}
