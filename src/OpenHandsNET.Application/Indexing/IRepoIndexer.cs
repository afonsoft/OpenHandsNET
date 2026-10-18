namespace OpenHandsNET.Application.Indexing;

public sealed record CodeUnitDto(
    Guid Id,
    Guid RepoIndexId,
    string Path,
    int StartLine,
    int EndLine,
    string Kind,
    string? SymbolName,
    string Text,
    string Hash);

public sealed record SearchCodeResult(
    CodeUnitDto Unit,
    double Score,
    string Snippet);

public interface ICodeSearchStore
{
    Task IndexUnitsAsync(Guid repoIndexId, IEnumerable<CodeUnitDto> units, CancellationToken ct = default);
    Task<IReadOnlyList<SearchCodeResult>> SearchAsync(Guid repoIndexId, string query, int limit = 10, CancellationToken ct = default);
    Task DeleteIndexUnitsAsync(Guid repoIndexId, CancellationToken ct = default);
}

public sealed record RepoIndexStatusDto(
    Guid Id,
    Guid? OrgId,
    string Repository,
    string Branch,
    string? CommitSha,
    string Status,
    string? StatsJson,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface IRepoIndexer
{
    Task<RepoIndexStatusDto> TriggerIndexAsync(string repository, string branch, Guid? orgId = null, CancellationToken ct = default);
    Task<RepoIndexStatusDto?> GetStatusAsync(Guid repoIndexId, CancellationToken ct = default);
    Task<RepoIndexStatusDto?> GetStatusByRepoAsync(string repository, string branch, Guid? orgId = null, CancellationToken ct = default);
}
