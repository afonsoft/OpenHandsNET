namespace Sandcastle.Application.Scm;

/// <summary>
/// SPEC-141 — CI autofix loop: <c>check_run|check_suite|workflow_run|
/// status</c> com conclusão de falha em PR de repo inscrito → run de fix
/// (conversa <c>agent_type: autofix</c>) que pusha na branch do PR até
/// verde ou até o cap de tentativas. Wire values são snake_case strings.
/// </summary>
public static class AutofixRunStatuses
{
    /// <summary>Run criado, aguardando o worker.</summary>
    public const string Queued = "queued";

    /// <summary>Worker ativo — coletando logs/spawnando tentativa.</summary>
    public const string Running = "running";

    /// <summary>Push enviado — aguardando o próximo check ficar terminal.</summary>
    public const string WaitingCheck = "waiting_check";

    /// <summary>Check verde após um fix — loop encerrado com sucesso.</summary>
    public const string Completed = "completed";

    /// <summary>Cap de tentativas, timeout ou erro do pipeline.</summary>
    public const string Failed = "failed";

    /// <summary>Falha não-autofixável (infra/timeout/flaky/permissão/segredo).</summary>
    public const string Blocked = "blocked";

    /// <summary>Cancelado via <c>POST /autofix-runs/{id}/cancel</c>.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>
    /// Status que indicam run ainda vivo (dedup/concurrency) — array p/
    /// tradução EF <c>Contains → IN</c>.
    /// </summary>
    public static readonly string[] Active =
        [Queued, Running, WaitingCheck];
}

/// <summary>SPEC-141 RF-004: outcome por tentativa (wire values).</summary>
public static class AutofixAttemptOutcomes
{
    /// <summary>Push gerou head verde.</summary>
    public const string Fixed = "fixed";

    /// <summary>Push enviado mas o check seguinte seguiu vermelho.</summary>
    public const string StillFailing = "still_failing";

    /// <summary>Falha classificada como não-autofixável.</summary>
    public const string Blocked = "blocked";

    /// <summary>Erro/timeout inesperado na tentativa.</summary>
    public const string Error = "error";

    /// <summary>Run cancelado durante a tentativa.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>SPEC-141: constantes do autofix (wire values).</summary>
public static class AutofixDefaults
{
    /// <summary>Commit-status context publicado pelo autofix.</summary>
    public const string StatusContext = "sandcastle/autofix";

    /// <summary><c>agent_type</c> das conversas de fix.</summary>
    public const string AgentType = "autofix";

    /// <summary>Prefixo dos check contexts do próprio Sandcastle (anti-loop).</summary>
    public const string OwnContextPrefix = "sandcastle/";
}

/// <summary>
/// SPEC-141 RF-001: evento de CI normalizado dos 4 payloads aceitos.
/// <see cref="Kind"/> ∈ <c>check_run|check_suite|workflow_run|status</c>.
/// </summary>
public sealed record CiCheckEvent(
    string Kind,
    string Repository,
    string HeadSha,
    string? HeadBranch,
    int? PrNumber,
    string CheckName,
    string Conclusion,
    string? CheckUrl,
    long? CheckRunId,
    long? WorkflowRunId,
    string? AppSlug,
    string? ActorLogin,
    string? HeadRepoFullName);

/// <summary>
/// SPEC-141 RF-003: um check run observado num ref (poll de
/// <c>check_runs</c> pós-push). <see cref="Status"/>: GitHub
/// (<c>queued|in_progress|completed</c>); <see cref="Conclusion"/>:
/// <c>success|failure|timed_out|cancelled|action_required|neutral|
/// skipped</c> (null enquanto não-terminal).
/// </summary>
public sealed record ScmCheckRun(
    long Id,
    string Name,
    string Status,
    string? Conclusion,
    string HeadSha,
    string? DetailsUrl);

/// <summary>SPEC-141 RF-001: PR associado a um commit (lookup por head SHA).</summary>
public sealed record ScmCommitPullRequest(
    int Number,
    string State,
    string HeadSha,
    string HeadRef,
    string HeadRepoFullName);

/// <summary>SPEC-141 RF-002: classificação do log de falha do check.</summary>
public enum CiFailureClass
{
    /// <summary>Lint/format — autofixável.</summary>
    Lint,

    /// <summary>Erros de tipo (tsc/mypy/CS) — autofixável.</summary>
    Type,

    /// <summary>Testes falhando — autofixável.</summary>
    Test,

    /// <summary>Erros de build/compilação — autofixável.</summary>
    Build,

    /// <summary>Infra do CI (docker, disco, rede, runner) — não autofixável.</summary>
    Infra,

    /// <summary>Timeout/cancelamento do job — não autofixável.</summary>
    Timeout,

    /// <summary>Assinatura flaky (heurística configurável) — não autofixável.</summary>
    Flaky,

    /// <summary>Permissão/acesso negado — não autofixável.</summary>
    Permission,

    /// <summary>Secret/env ausente — não autofixável.</summary>
    Secrets,

    /// <summary>Não classificado — autofixável (deixa o agente tentar).</summary>
    Unknown,
}

/// <summary>
/// SPEC-141 RF-004: run persistido (visão de API).
/// </summary>
public sealed record AutofixRun(
    Guid Id,
    Guid? OrgId,
    Guid ConnectionId,
    string Repository,
    int PrNumber,
    string HeadSha,
    string? HeadBranch,
    string CheckName,
    string CheckKind,
    string Status,
    int AttemptCount,
    Guid? ConversationId,
    string Trigger,
    string? RequestedByUserId,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

/// <summary>SPEC-141 RF-004: tentativa persistida (visão de API).</summary>
public sealed record AutofixAttempt(
    Guid Id,
    Guid RunId,
    int Number,
    string HeadShaIn,
    string? HeadShaOut,
    string? FailedChecksJson,
    string? Outcome,
    string? LogDigest,
    Guid? ConversationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

/// <summary>Run + attempts para <c>GET /scm/autofix-runs/{id}</c>.</summary>
public sealed record AutofixRunDetail(
    AutofixRun Run,
    IReadOnlyList<AutofixAttempt> Attempts);

/// <summary>Página cursada de runs.</summary>
public sealed record AutofixRunPage(
    IReadOnlyList<AutofixRun> Items,
    string? NextPageId);

/// <summary>Falhas possíveis do trigger de autofix.</summary>
public enum AutofixTriggerError
{
    /// <summary>404 — PR não encontrado (manual).</summary>
    NotFound,

    /// <summary>404 — nenhuma conexão cobre o repo.</summary>
    NoConnection,

    /// <summary>409 — já existe run ativo p/ o PR ou mesmo (head,check).</summary>
    Duplicate,

    /// <summary>403 — autofix desabilitado / repo não inscrito.</summary>
    Disabled,

    /// <summary>200 — evento filtrado por política (não-falha, bot, escopo).</summary>
    Ignored,
}

/// <summary>Resultado do trigger (run criado ou erro).</summary>
public sealed record AutofixTriggerResult(
    Guid? RunId, AutofixTriggerError? Error, string? Detail)
{
    /// <summary>Run criado e enfileirado.</summary>
    public static AutofixTriggerResult Ok(Guid id) => new(id, null, null);

    /// <summary>Falha/skip de trigger.</summary>
    public static AutofixTriggerResult Fail(
        AutofixTriggerError e, string? detail = null) => new(null, e, detail);
}

/// <summary>
/// SPEC-141 RF-001/RF-002/RF-004: serviço de runs — trigger por evento
/// de CI (normalizado) com resolução de PR + política anti-loop, trigger
/// manual, listagem, detalhe e cancel.
/// </summary>
public interface IAutofixService
{
    /// <summary>
    /// Trigger a partir de um <see cref="CiCheckEvent"/> já normalizado:
    /// aplica a política (conclusão de falha, escopo do repo, gate de
    /// check-name, anti-loop por app/autor/head_sha_out), resolve o PR
    /// (payload → commits/{sha}/pulls → conversations.PullRequestHeadSha)
    /// e enfileira o run. Skips retornam <see cref="AutofixTriggerError.Ignored"/>.
    /// </summary>
    Task<AutofixTriggerResult> TriggerCiEventAsync(
        ScmConnection connection, CiCheckEvent ev, CancellationToken ct);

    /// <summary>
    /// Trigger manual (<c>POST /scm/autofix</c>) — exige repo inscrito
    /// (<see cref="ScmConnection.AutofixCovers"/>) e PR aberto.
    /// </summary>
    Task<AutofixTriggerResult> TriggerManualAsync(
        ScmConnection connection, string repository, int prNumber,
        string? requestedByUserId, CancellationToken ct);

    /// <summary>Lista runs da org (filtros repository/pr/status + cursor).</summary>
    Task<AutofixRunPage> SearchAsync(
        Guid orgId, string? repository, int? prNumber, string? status,
        string? cursor, int limit, CancellationToken ct);

    /// <summary>Detalhe de um run com attempts (null = não encontrado).</summary>
    Task<AutofixRunDetail?> GetAsync(Guid orgId, Guid id, CancellationToken ct);

    /// <summary>
    /// Cancela um run ativo (queued|running|waiting_check) — false =
    /// não encontrado ou já terminal.
    /// </summary>
    Task<bool> CancelAsync(Guid orgId, Guid id, CancellationToken ct);
}

/// <summary>Fila in-process de runs — drenada pelo worker hosted.</summary>
public interface IAutofixDispatcher
{
    /// <summary>Enfileira um run para execução.</summary>
    ValueTask EnqueueAsync(Guid autofixRunId, CancellationToken ct);
}

/// <summary>
/// SPEC-141 RF-003: job do autofix — loop logs → classificação →
/// conversa de fix (push na branch do PR) → poll de checks até
/// verde/cap/blocked/cancelled.
/// </summary>
public interface IAutofixRunner
{
    /// <summary>Executa um run (idempotente por run id).</summary>
    Task RunAsync(Guid autofixRunId, CancellationToken ct);
}

/// <summary>
/// SPEC-141: patch dos campos de autofix da conexão — qualquer campo
/// null significa "não alterar" (listas vazias = sem restrição).
/// </summary>
public sealed record ScmConnectionAutofixUpdate(
    bool? AutofixEnabled,
    IReadOnlyList<string>? AutofixRepos,
    IReadOnlyList<string>? AutofixCheckPatterns,
    int? AutofixMaxAttempts);