namespace OpenHandsNET.Application.Ask;

public sealed record AskRequest(
    string Repository,
    string Question,
    string? Branch = null,
    Guid? OrgId = null);

public sealed record AskCitation(
    string Path,
    int StartLine,
    int EndLine,
    string Snippet);

public sealed record AskResponse(
    Guid SessionId,
    string Answer,
    IReadOnlyList<AskCitation> Citations,
    bool CanPromoteToAgent);

public sealed record PromoteAskSessionRequest(
    Guid SessionId,
    string? Title = null);

public sealed record PromoteAskSessionResponse(
    Guid ConversationId,
    string TaskStatus);

public interface IAskModeService
{
    Task<AskResponse> AskAsync(AskRequest request, CancellationToken ct = default);
    Task<PromoteAskSessionResponse> PromoteToAgentAsync(PromoteAskSessionRequest request, CancellationToken ct = default);
}