namespace OpenHandsNET.Application.Indexing;

/// <summary>
/// SPEC-60 RF-001: status de um índice de repositório.
/// </summary>
public enum RepoIndexStatus
{
    Indexing,
    Ready,
    Stale,
    Failed,
}

/// <summary>Summary de um repo indexado (listagem/admin).</summary>
public sealed record RepoIndexInfo(
    Guid Id, Guid? OrgId, string Repository, string Branch,
    string? CommitSha, RepoIndexStatus Status, string? Error,
    int UnitCount, int FileCount, DateTimeOffset CreatedAt,
    DateTimeOffset? IndexedAt);

/// <summary>Unidade de código indexada (símbolo ou chunk).</summary>
public sealed record CodeUnit(
    string Path, int StartLine, int EndLine, string Kind, string Text,
    string Hash);

/// <summary>Resultado de busca com score.</summary>
public sealed record CodeUnitHit(CodeUnit Unit, double Score);

/// <summary>
/// Busca full-text sobre as unidades de um índice — dupla impl:
/// FTS5 no SQLite (tabela shadow), ILIKE no Postgres.
/// </summary>
public interface ICodeSearchStore
{
    Task<IReadOnlyList<CodeUnitHit>> SearchAsync(
        Guid repoIndexId, string query, int limit, CancellationToken ct);
}

/// <summary>
/// (Re)indexação: clone shallow + chunking + persistência atômica das
/// unidades. Roda em host worker (SPEC-60 RF-001) — sandbox dedicado
/// seria overkill para uma operação read-only de minutos.
/// </summary>
public interface IRepoIndexer
{
    Task IndexAsync(Guid repoIndexId, CancellationToken ct);
}

/// <summary>Admin/ops sobre índices.</summary>
public interface IRepoIndexService
{
    Task<IReadOnlyList<RepoIndexInfo>> ListAsync(CancellationToken ct);
    Task<RepoIndexInfo> AddAsync(
        string repository, string? branch, CancellationToken ct);
    Task<RepoIndexInfo?> GetAsync(Guid id, CancellationToken ct);
    Task<RepoIndexInfo?> GetByRepoAsync(
        string repository, CancellationToken ct);
    Task<bool> RequestReindexAsync(Guid id, CancellationToken ct);
    Task MarkStaleAsync(string repository, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    Task<string?> ReadFileAsync(
        Guid id, string path, CancellationToken ct);
}

/// <summary>Fila de reindex (worker hosted).</summary>
public interface IRepoIndexQueue
{
    void Enqueue(Guid repoIndexId);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct);
}

/// <summary>Citação resolvida de uma resposta Ask.</summary>
public sealed record AskCitation(string Path, int StartLine, int EndLine);

/// <summary>Mensagem persistida de uma ask-session.</summary>
public sealed record AskMessage(
    string Role, string Text, IReadOnlyList<AskCitation> Citations,
    DateTimeOffset At);

/// <summary>DTO de ask-session para a API.</summary>
public sealed record AskSessionInfo(
    Guid Id, Guid? OrgId, string Repository, string? Title,
    IReadOnlyList<AskMessage> Messages, Guid? PromotedConversationId,
    DateTimeOffset CreatedAt);

/// <summary>
/// SPEC-60 RF-002: pipeline Ask — code_search → LLM → resposta com
/// citações obrigatórias. Read-only por construção.
/// </summary>
public interface IAskService
{
    Task<AskSessionInfo> CreateAsync(
        string repository, string question, CancellationToken ct);
    Task<AskSessionInfo> AskAsync(
        Guid sessionId, string question, CancellationToken ct);
    Task<AskSessionInfo?> GetAsync(Guid sessionId, CancellationToken ct);
    Task<IReadOnlyList<AskSessionInfo>> ListAsync(CancellationToken ct);
    /// <summary>Ask→Agent: gera plan_document e inicia conversa normal.</summary>
    Task<Guid> PromoteAsync(Guid sessionId, CancellationToken ct);
}
