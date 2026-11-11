namespace Sandcastle.Application.Ask;

public sealed record AskRequest(
    string Repository,
    string Question,
    string? Branch = null,
    Guid? OrgId = null);

/// <summary>
/// Uma citação de código. <paramref name="Permalink"/> (SPEC-145) é o
/// link do provider <c>{repo}/blob/{sha}/{path}#Lx-Ly</c> quando o
/// índice tem CommitSha.
/// </summary>
public sealed record AskCitation(
    string Path,
    int StartLine,
    int EndLine,
    string Snippet,
    string? Permalink = null);

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