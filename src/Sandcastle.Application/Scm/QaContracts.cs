namespace Sandcastle.Application.Scm;

/// <summary>
/// SPEC-142 — QA agent com evidência: pipeline
/// <c>Understand → Setup → Exercise → Report</c> que executa o software
/// alterado por um PR (browser, CLI, API) e publica veredicto
/// <c>PASS|FAIL|PARTIAL</c> + evidência (screenshots, vídeo) no PR.
/// Wire values são snake_case/lower strings (mesma convenção dos demais
/// registros do domínio).
/// </summary>
public static class QaRunStatuses
{
    /// <summary>Run criado, aguardando o worker.</summary>
    public const string Queued = "queued";

    /// <summary>Worker executando o pipeline de 4 fases.</summary>
    public const string Running = "running";

    /// <summary>Pipeline terminou — <c>verdict</c> carrega o outcome.</summary>
    public const string Completed = "completed";

    /// <summary>Falha de infraestrutura do pipeline (provider, sandbox).</summary>
    public const string Failed = "failed";

    /// <summary>
    /// Status que contam para dedup por <c>(repo, pr, head_sha)</c> —
    /// run ativo ou já concluído cobre o head (mesmo modelo do review).
    /// </summary>
    public static readonly string[] Dedup =
        [Queued, Running, Completed];
}

/// <summary>SPEC-142 RF-002/RF-004: veredicto do run (wire values).</summary>
public static class QaVerdicts
{
    /// <summary>Todos os passos planejados executados com sucesso.</summary>
    public const string Pass = "pass";

    /// <summary>Ao menos um passo executado falhou (asserção/exit code).</summary>
    public const string Fail = "fail";

    /// <summary>
    /// Bail-out honesto: passos não executados (caps, app não subiu,
    /// browser indisponível) — nunca promovido a <see cref="Pass"/>.
    /// </summary>
    public const string Partial = "partial";
}

/// <summary>SPEC-142 RF-002: classificação da mudança (wire values).</summary>
public static class QaClassifications
{
    /// <summary>Nova funcionalidade.</summary>
    public const string Feature = "feature";

    /// <summary>Correção de bug — reproduce-on-base quando viável.</summary>
    public const string Bugfix = "bugfix";

    /// <summary>Refatoração sem mudança de comportamento.</summary>
    public const string Refactor = "refactor";

    /// <summary>Config/infra/docs — smoke-test mínimo.</summary>
    public const string Config = "config";

    /// <summary>LLM não classificou — plano mínimo segue.</summary>
    public const string Unknown = "unknown";

    /// <summary>Todos os valores válidos.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        { Feature, Bugfix, Refactor, Config, Unknown };
}

/// <summary>SPEC-142 RF-002: outcome por passo (wire values).</summary>
public static class QaStepOutcomes
{
    /// <summary>Passo executado com sucesso (asserções satisfeitas).</summary>
    public const string Ok = "ok";

    /// <summary>Passo executado e falhou.</summary>
    public const string Fail = "fail";

    /// <summary>Não executado — bail-out (cap, timeout, dependência).</summary>
    public const string Skipped = "skipped";

    /// <summary>Outcome de infra (clone/setup/start) que aborta o run.</summary>
    public const string InfraFail = "infra_fail";
}

/// <summary>SPEC-142: constantes do QA agent (wire values).</summary>
public static class QaDefaults
{
    /// <summary>Commit-status context publicado pelo QA.</summary>
    public const string StatusContext = "sandcastle/qa";

    /// <summary>
    /// Status é <c>success</c> quando o run terminou — o veredicto não
    /// condiciona o status (default advisory).
    /// </summary>
    public const string PassConditionCompleted = "completed";

    /// <summary>Status <c>failure</c> a menos que o veredicto seja PASS.</summary>
    public const string PassConditionPassed = "passed";

    /// <summary>Artifact kinds (sufixo do nome no artifact store).</summary>
    public const string ArtifactKindScreenshot = "qa_screenshot";

    /// <summary>Vídeo da sessão browser (webm).</summary>
    public const string ArtifactKindVideo = "qa_video";
}

/// <summary>
/// SPEC-142 RF-002: um passo do plano de teste mínimo gerado pela fase
/// Understand (LLM) — <see cref="Kind"/> ∈ <c>exec|browser</c>;
/// <see cref="Action"/> browser ∈ <c>goto|click|type|scroll|back|forward|
/// wait|screenshot</c>. <see cref="Expect"/> é uma asserção simples
/// (<c>contains:</c>, <c>not_contains:</c>, <c>exit:</c>,
/// <c>url_contains:</c>, <c>status:</c>) avaliada deterministicamente.
/// </summary>
public sealed record QaPlanStep(
    string Kind,
    string? Name,
    string? Action,
    string? Command,
    string? Url,
    string? Selector,
    string? Text,
    string? Expect,
    int? TimeoutSeconds);

/// <summary>
/// SPEC-142 RF-002: como subir a app no sandbox (fase Setup) —
/// <see cref="Command"/> roda em background; readiness via
/// <c>http://localhost:{Port}{Path}</c>.
/// </summary>
public sealed record QaPlanStart(
    string Command,
    int Port,
    string? Path);

/// <summary>
/// SPEC-142 RF-002: reprodução de bugfix no base — comando que evidencia
/// o bug quando <see cref="Indicator"/> (asserção do mesmo vocabulário
/// de <see cref="QaPlanStep.Expect"/>) é satisfeita.
/// </summary>
public sealed record QaPlanReproduction(
    string Command,
    string? Indicator);

/// <summary>
/// SPEC-142 RF-002: plano de teste mínimo persistido em
/// <c>qa_runs.plan_json</c> — classificação + entry points + passos.
/// </summary>
public sealed record QaPlan(
    string Classification,
    string? Summary,
    QaPlanStart? Start,
    IReadOnlyList<QaPlanStep> Steps,
    QaPlanReproduction? Reproduction);

/// <summary>Outcome de um passo browser executado pelo driver.</summary>
public sealed record QaBrowserStepResult(
    bool Ok,
    string? Detail,
    byte[]? Screenshot,
    string? Content);

/// <summary>
/// Resultado de um batch de passos browser consecutivos (uma sessão,
/// um vídeo). <see cref="Executed"/> false = driver/browser indisponível
/// — caller marca os passos como skipped.
/// </summary>
public sealed record QaBrowserRunResult(
    bool Executed,
    string? Error,
    IReadOnlyList<QaBrowserStepResult> Steps,
    byte[]? Video);

/// <summary>
/// SPEC-142 RF-003 — seam do driver browser (Playwright dentro do
/// sandbox, SPEC-131): mockável em unit tests — nenhum teste exige
/// browser real. Um batch = uma sessão browser + uma gravação de vídeo.
/// </summary>
public interface IQaBrowserDriver
{
    /// <summary>
    /// Executa um batch de passos browser contra <paramref name="baseUrl"/>
    /// (app já rodando no sandbox) e retorna outcomes por passo +
    /// screenshots PNG + vídeo webm quando disponível.
    /// </summary>
    Task<QaBrowserRunResult> RunStepsAsync(
        string baseUrl, IReadOnlyList<QaPlanStep> steps,
        CancellationToken ct);
}

/// <summary>Fábrica de drivers browser vinculados a um sandbox.</summary>
public interface IQaBrowserDriverFactory
{
    /// <summary>Driver para o sandbox informado (nunca null).</summary>
    IQaBrowserDriver Create(Domain.Sandbox.Sandbox sandbox);
}

/// <summary>SPEC-142 RF-004: run persistido (visão de API).</summary>
public sealed record QaRun(
    Guid Id,
    Guid? OrgId,
    Guid ConnectionId,
    string Repository,
    int PrNumber,
    string HeadSha,
    string? BaseSha,
    string Status,
    string? Verdict,
    string? Classification,
    bool PartialPlan,
    Guid? ConversationId,
    string Trigger,
    string? RequestedByUserId,
    string? Error,
    int StepCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

/// <summary>SPEC-142 RF-003: passo executado (visão de API).</summary>
public sealed record QaStep(
    Guid Id,
    Guid QaRunId,
    int Number,
    string Action,
    string? Target,
    string Outcome,
    string? Detail,
    string? ArtifactRef,
    long? DurationMs,
    DateTimeOffset CreatedAt);

/// <summary>Run + plano + passos + evidence links para <c>GET /qa/runs/{id}</c>.</summary>
public sealed record QaRunDetail(
    QaRun Run,
    QaPlan? Plan,
    IReadOnlyList<QaStep> Steps,
    IReadOnlyList<QaEvidenceLink> Evidence);

/// <summary>Link de evidência auth'd (artifact endpoint do próprio run).</summary>
public sealed record QaEvidenceLink(
    string Kind, string Name, string Url);

/// <summary>Página cursada de runs.</summary>
public sealed record QaRunPage(
    IReadOnlyList<QaRun> Items,
    string? NextPageId);

/// <summary>Falhas possíveis do trigger de QA.</summary>
public enum QaTriggerError
{
    /// <summary>404 — PR não encontrado (manual).</summary>
    NotFound,

    /// <summary>404 — nenhuma conexão cobre o repo.</summary>
    NoConnection,

    /// <summary>409 — já existe run para o mesmo (repo, pr, head_sha).</summary>
    Duplicate,

    /// <summary>403 — quota diária de runs por repo excedida.</summary>
    QuotaExceeded,

    /// <summary>403 — QA desabilitado na conexão.</summary>
    Disabled,
}

/// <summary>Resultado do trigger (run criado ou erro).</summary>
public sealed record QaTriggerResult(
    Guid? QaRunId, QaTriggerError? Error, string? Detail)
{
    /// <summary>Run criado e enfileirado.</summary>
    public static QaTriggerResult Ok(Guid id) => new(id, null, null);

    /// <summary>Falha de trigger.</summary>
    public static QaTriggerResult Fail(
        QaTriggerError e, string? detail = null) => new(null, e, detail);
}

/// <summary>
/// SPEC-142 RF-001/RF-004: serviço de runs — trigger (dedup por
/// <c>(repo, pr, head_sha)</c> + quota + enrollment), trigger manual,
/// listagem e detalhe (run + plano + passos + evidência).
/// </summary>
public interface IQaService
{
    /// <summary>
    /// Cria um run para (repo, pr, head) e o enfileira. Dedup por
    /// <c>(repo, pr, head_sha)</c> — run existente (queued|running|
    /// completed) retorna <see cref="QaTriggerError.Duplicate"/>.
    /// </summary>
    Task<QaTriggerResult> TriggerAsync( // NOSONAR S107 — contexto de trigger único; param object só desloca aridade
        ScmConnection connection, string repository, int prNumber,
        string headSha, string? baseSha, string trigger,
        string? requestedByUserId, CancellationToken ct);

    /// <summary>
    /// Trigger manual (<c>POST /qa/runs</c>) — exige repo inscrito
    /// (<see cref="ScmConnection.QaCovers"/>) e PR aberto; resolve o
    /// head via provider.
    /// </summary>
    Task<QaTriggerResult> TriggerManualAsync(
        ScmConnection connection, string repository, int prNumber,
        string? requestedByUserId, CancellationToken ct);

    /// <summary>Lista runs da org (filtros repository/pr/verdict + cursor).</summary>
    Task<QaRunPage> SearchAsync(
        Guid orgId, string? repository, int? prNumber, string? verdict,
        string? cursor, int limit, CancellationToken ct);

    /// <summary>Detalhe de um run com plano/passos/evidência (null = não encontrado).</summary>
    Task<QaRunDetail?> GetAsync(Guid orgId, Guid id, CancellationToken ct);
}

/// <summary>Fila in-process de runs — drenada pelo worker hosted.</summary>
public interface IQaDispatcher
{
    /// <summary>Enfileira um run para execução.</summary>
    ValueTask EnqueueAsync(Guid qaRunId, CancellationToken ct);
}

/// <summary>
/// SPEC-142 RF-002: job do QA — pipeline Understand → Setup → Exercise →
/// Report (não é conversa visível; o sandbox é descartável por run).
/// </summary>
public interface IQaRunner
{
    /// <summary>Executa um run (idempotente por run id).</summary>
    Task RunAsync(Guid qaRunId, CancellationToken ct);
}

/// <summary>
/// SPEC-142: patch dos campos de QA da conexão — qualquer campo null
/// significa "não alterar" (lista vazia = todos os repos da conexão).
/// </summary>
public sealed record ScmConnectionQaUpdate(
    bool? QaEnabled,
    IReadOnlyList<string>? QaRepos,
    string? QaPassCondition);