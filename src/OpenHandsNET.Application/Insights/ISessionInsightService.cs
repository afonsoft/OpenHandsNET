namespace OpenHandsNET.Application.Insights;

public sealed record SessionIssue(
    string Kind,
    string Severity,
    string Summary,
    IReadOnlyList<string>? EventIds = null);

public sealed record PhaseCost(
    string Phase,
    long Tokens,
    double Seconds);

public sealed record SessionInsightDto(
    Guid Id,
    Guid ConversationId,
    Guid? OrgId,
    string Outcome,
    IReadOnlyList<SessionIssue> Issues,
    IReadOnlyList<string> DeadEnds,
    IReadOnlyList<PhaseCost> PhaseCosts,
    string? SuggestedPrompt,
    IReadOnlyList<string> Learnings,
    string? Model,
    DateTimeOffset CreatedAt);

public sealed record InsightsStatsDto(
    int TotalSessionsAnalyzed,
    int SuccessCount,
    int PartialCount,
    int FailedCount,
    int UnclearCount,
    double SuccessRate,
    IReadOnlyDictionary<string, int> TopIssues);

public interface ISessionInsightService
{
    Task<SessionInsightDto> AnalyzeSessionAsync(Guid conversationId, bool force = false, CancellationToken ct = default);
    Task<SessionInsightDto?> GetSessionInsightAsync(Guid conversationId, CancellationToken ct = default);
    Task<InsightsStatsDto> GetStatsAsync(Guid? orgId = null, CancellationToken ct = default);
}