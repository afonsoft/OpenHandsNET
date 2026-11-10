namespace Sandcastle.Application.Scm;

/// <summary>
/// SPEC-140 — auto-review de PR: pipeline trigger → run (diff → rules →
/// LLM → findings validados) → publish (review inline + commit status).
/// Wire values são snake_case strings (mesma convenção dos demais
/// registros do domínio).
/// </summary>
public static class ReviewRunStatuses
{
    /// <summary>Run criado, aguardando o worker.</summary>
    public const string Queued = "queued";

    /// <summary>Worker executando o pipeline.</summary>
    public const string Running = "running";

    /// <summary>Review postado (ou zero findings) com sucesso.</summary>
    public const string Completed = "completed";

    /// <summary>Dedup: mesmo diff já revisado (sem nova postagem).</summary>
    public const string Skipped = "skipped";

    /// <summary>Pipeline falhou (provider, LLM ou posting).</summary>
    public const string Failed = "failed";
}

/// <summary>SPEC-140 RF-002: severidade do finding (wire values).</summary>
public static class ReviewSeverities
{
    /// <summary>Bug/incidente provável — bloqueio recomendado.</summary>
    public const string Critical = "critical";

    /// <summary>Problema real que deve ser resolvido.</summary>
    public const string Important = "important";

    /// <summary>Melhoria concreta sugerida.</summary>
    public const string Suggestion = "suggestion";

    /// <summary>Nit — opcional.</summary>
    public const string Nit = "nit";

    /// <summary>Todos os valores válidos.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        { Critical, Important, Suggestion, Nit };
}

/// <summary>SPEC-140 RF-002: categoria do finding (wire values).</summary>
public static class ReviewFindingKinds
{
    /// <summary>Defeito de correção/lógica.</summary>
    public const string Bug = "bug";

    /// <summary>Vulnerabilidade ou exposição de segredo.</summary>
    public const string Security = "security";

    /// <summary>Performance.</summary>
    public const string Perf = "perf";

    /// <summary>Estilo/legibilidade.</summary>
    public const string Style = "style";

    /// <summary>Ponto que pede verificação humana.</summary>
    public const string Investigate = "investigate";

    /// <summary>Nota informativa.</summary>
    public const string Note = "note";

    /// <summary>Todos os valores válidos.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        { Bug, Security, Perf, Style, Investigate, Note };
}

/// <summary>SPEC-140 RF-004: lifecycle do finding (wire values).</summary>
public static class ReviewFindingStatuses
{
    /// <summary>Aberto (default).</summary>
    public const string Open = "open";

    /// <summary>Resolvido em push posterior.</summary>
    public const string Fixed = "fixed";

    /// <summary>Descartado pelo usuário (feedback de tuning).</summary>
    public const string Dismissed = "dismissed";

    /// <summary>Obsoleto — linha do diff saiu de escopo.</summary>
    public const string Stale = "stale";

    /// <summary>Valores aceitos no PATCH de findings.</summary>
    public static readonly IReadOnlySet<string> Updatable =
        new HashSet<string>(StringComparer.Ordinal)
        { Open, Dismissed };
}

/// <summary>SPEC-140 RF-004: run persistido (visão de API).</summary>
public sealed record ReviewRun(
    Guid Id,
    Guid? OrgId,
    Guid ConnectionId,
    string Repository,
    int PrNumber,
    string HeadSha,
    string? DiffHash,
    string Status,
    bool Partial,
    string Trigger,
    string? RequestedByUserId,
    string? Error,
    int FindingCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

/// <summary>SPEC-140 RF-002/RF-004: finding validado + persistido.</summary>
public sealed record ReviewFinding(
    Guid Id,
    Guid ReviewRunId,
    string Repository,
    int PrNumber,
    string Kind,
    string Severity,
    double? Confidence,
    string Path,
    int? Line,
    string? Title,
    string Body,
    string? SuggestedPatch,
    string Status,
    string? CommentUrl,
    DateTimeOffset CreatedAt);

/// <summary>Run + findings para <c>GET /scm/reviews/{id}</c>.</summary>
public sealed record ReviewRunDetail(
    ReviewRun Run,
    IReadOnlyList<ReviewFinding> Findings);

/// <summary>Página cursada de runs.</summary>
public sealed record ReviewRunPage(
    IReadOnlyList<ReviewRun> Items,
    string? NextPageId);

/// <summary>Falhas possíveis do trigger de review.</summary>
public enum ReviewTriggerError
{
    /// <summary>404 — conexão/PR não encontrado.</summary>
    NotFound,

    /// <summary>404 — nenhuma conexão cobre o repo.</summary>
    NoConnection,

    /// <summary>409 — já existe run ativo/completo p/ mesmo head.</summary>
    Duplicate,

    /// <summary>403 — quota diária de reviews por repo excedida.</summary>
    QuotaExceeded,

    /// <summary>403 — auto-review desabilitado na conexão.</summary>
    Disabled,
}

/// <summary>Resultado do trigger (run criado ou erro).</summary>
public sealed record ReviewTriggerResult(
    Guid? ReviewRunId, ReviewTriggerError? Error, string? Detail)
{
    /// <summary>Run criado e enfileirado.</summary>
    public static ReviewTriggerResult Ok(Guid id) => new(id, null, null);

    /// <summary>Falha de trigger.</summary>
    public static ReviewTriggerResult Fail(
        ReviewTriggerError e, string? detail = null) => new(null, e, detail);
}

/// <summary>
/// SPEC-140 RF-002: serviço de runs — trigger (dedup + quota + enqueue),
/// listagem, detalhe e PATCH de finding.
/// </summary>
public interface IReviewService
{
    /// <summary>
    /// Cria um run para (repo, pr, head) e o enfileira. Dedup por
    /// <c>(repo, pr, head_sha)</c> — run existente retorna
    /// <see cref="ReviewTriggerError.Duplicate"/>.
    /// </summary>
    Task<ReviewTriggerResult> TriggerAsync(
        ScmConnection connection, string repository, int prNumber,
        string headSha, string trigger, string? requestedByUserId,
        CancellationToken ct);

    /// <summary>Lista runs da org (filtros repository/pr/status + cursor).</summary>
    Task<ReviewRunPage> SearchAsync(
        Guid orgId, string? repository, int? prNumber, string? status,
        string? cursor, int limit, CancellationToken ct);

    /// <summary>Detalhe de um run com findings (null = não encontrado).</summary>
    Task<ReviewRunDetail?> GetAsync(Guid orgId, Guid id, CancellationToken ct);

    /// <summary>
    /// Atualiza o status de um finding (<c>open|dismissed</c>) —
    /// null = não encontrado.
    /// </summary>
    Task<ReviewFinding?> UpdateFindingStatusAsync(
        Guid orgId, Guid runId, Guid findingId, string status,
        CancellationToken ct);
}

/// <summary>Fila in-process de runs — drenada pelo worker hosted.</summary>
public interface IReviewDispatcher
{
    /// <summary>Enfileira um run para execução.</summary>
    ValueTask EnqueueAsync(Guid reviewRunId, CancellationToken ct);
}

/// <summary>
/// SPEC-140 RF-002: job próprio do review (não é conversa visível) —
/// executa fetch diff → rules → LLM → findings → posting + status.
/// </summary>
public interface IReviewRunner
{
    /// <summary>Executa um run (idempotente por run id).</summary>
    Task RunAsync(Guid reviewRunId, CancellationToken ct);
}