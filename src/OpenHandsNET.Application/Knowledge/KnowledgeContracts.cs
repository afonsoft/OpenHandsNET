namespace OpenHandsNET.Application.Knowledge;

/// <summary>SPEC-63: estado da sugestão na fila de revisão.</summary>
public enum SuggestionStatus { Pending, Approved, Dismissed }

/// <summary>Tipo da sugestão: entry nova, merge proposto, conflito.</summary>
public enum SuggestionKind { Entry, Merge, Conflict }

public sealed record KnowledgeSuggestionInfo(
    Guid Id, Guid? OrgId, string Scope, string? RepoPath,
    string Title, string Content, SuggestionKind Kind,
    SuggestionStatus Status, string? Reason,
    IReadOnlyList<Guid> SourceConversationIds,
    IReadOnlyList<Guid> SourceEntryIds,
    string? Resolution, DateTimeOffset CreatedAt);

/// <summary>Relatório do groom (RF-003).</summary>
public sealed record KnowledgeGroomReport(
    IReadOnlyList<(Guid A, Guid B, double Similarity)> Duplicates,
    IReadOnlyList<(Guid A, Guid B)> Conflicts,
    IReadOnlyList<Guid> Stale);

public interface IKnowledgeService
{
    Task<IReadOnlyList<KnowledgeSuggestionInfo>> ListSuggestionsAsync(
        SuggestionStatus? status, string? scope, CancellationToken ct);
    Task<KnowledgeSuggestionInfo> SuggestAsync(
        string title, string content, string scope, string? repoPath,
        SuggestionKind kind, string? reason,
        IReadOnlyList<Guid>? sourceConversationIds,
        IReadOnlyList<Guid>? sourceEntryIds, CancellationToken ct);
    Task<KnowledgeSuggestionInfo> ApproveAsync(
        Guid id, string? contentOverride, string? actorUserId,
        CancellationToken ct);
    Task<KnowledgeSuggestionInfo> DismissAsync(
        Guid id, string? reason, CancellationToken ct);
    Task<KnowledgeGroomReport> GroomAsync(CancellationToken ct);
}
