namespace Sandcastle.Application.Scm;

/// <summary>SCM provider kinds (SPEC-52; gitlab|bitbucket|azuredevops — SPEC-53).</summary>
public enum ScmKind
{
    /// <summary>GitHub (App + webhooks).</summary>
    GitHub,

    /// <summary>GitLab (SPEC-53).</summary>
    GitLab,

    /// <summary>Bitbucket (SPEC-53).</summary>
    Bitbucket,

    /// <summary>Azure DevOps (SPEC-53).</summary>
    AzureDevOps,
}

/// <summary>Lowercase wire name ↔ <see cref="ScmKind"/>.</summary>
public static class ScmKindExtensions
{
    /// <summary>Wire name (e.g. <c>github</c>).</summary>
    public static string ToWire(this ScmKind kind) => kind switch
    {
        ScmKind.GitHub => "github",
        ScmKind.GitLab => "gitlab",
        ScmKind.Bitbucket => "bitbucket",
        ScmKind.AzureDevOps => "azuredevops",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Parses a wire name (null when unknown).</summary>
    public static ScmKind? Parse(string? wire) => wire switch
    {
        "github" => ScmKind.GitHub,
        "gitlab" => ScmKind.GitLab,
        "bitbucket" => ScmKind.Bitbucket,
        "azuredevops" => ScmKind.AzureDevOps,
        _ => null,
    };
}

/// <summary>
/// Org-level SCM connection (resolved entity — secrets by-ref only).
/// SPEC-53: ApiUrl = base do provider (self-managed/gitlab.com, bitbucket
/// cloud|DC, dev.azure.com/{org}); AuthKind = pat|app|oauth|entra_sp;
/// MetaJson = project keys/workspace/collection extras.
/// SPEC-140: campos <c>Review*</c> = enrollment do auto-review de PR.
/// </summary>
public sealed record ScmConnection(
    Guid Id,
    Guid? OrgId,
    ScmKind Kind,
    string? InstallationId,
    string? PemSecretRef,
    string? WebhookSecretRef,
    IReadOnlyList<string> Repos,
    string? AppId,
    string? AppSlug,
    string? CreatedByUserId,
    DateTimeOffset CreatedAt,
    string? ApiUrl = null,
    string? AuthKind = null,
    string? MetaJson = null,
    bool ReviewEnabled = false,
    IReadOnlyList<string>? ReviewRepos = null,
    string? ReviewTriggerMode = null,
    string? ReviewPassCondition = null,
    bool AutofixEnabled = false,
    IReadOnlyList<string>? AutofixRepos = null,
    IReadOnlyList<string>? AutofixCheckPatterns = null,
    int? AutofixMaxAttempts = null)
{
    /// <summary>
    /// SPEC-140 RF-001: repos cobertos pelo auto-review — vazio/null =
    /// todos os repos da conexão.
    /// </summary>
    public bool ReviewCovers(string repository) =>
        ReviewEnabled
        && (ReviewRepos is null || ReviewRepos.Count == 0
            || ReviewRepos.Contains(repository,
                StringComparer.OrdinalIgnoreCase));

    /// <summary><c>all_prs</c> (default) | <c>label_only</c>.</summary>
    public string EffectiveTriggerMode =>
        string.IsNullOrEmpty(ReviewTriggerMode)
            ? ReviewDefaults.TriggerModeAll
            : ReviewTriggerMode!;

    /// <summary><c>completed</c> (default) | <c>no_critical</c> | <c>no_findings</c>.</summary>
    public string EffectivePassCondition =>
        string.IsNullOrEmpty(ReviewPassCondition)
            ? ReviewDefaults.PassConditionCompleted
            : ReviewPassCondition!;

    /// <summary>
    /// SPEC-141 RF-001: repos inscritos no CI autofix — vazio/null =
    /// todos os repos da conexão.
    /// </summary>
    public bool AutofixCovers(string repository) =>
        AutofixEnabled
        && (AutofixRepos is null || AutofixRepos.Count == 0
            || AutofixRepos.Contains(repository,
                StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// SPEC-141 RF-002: gate de escopo por check-name — padrões glob
    /// (<c>*</c> wildcard) da conexão ou, na ausência, os globais
    /// (<c>Autofix:CheckNamePatterns</c>); default <c>*</c> = tudo.
    /// </summary>
    public bool AutofixAllowsCheck(
        string checkName, IReadOnlyList<string>? globalPatterns)
    {
        var patterns = AutofixCheckPatterns is { Count: > 0 } local
            ? local : globalPatterns;
        if (patterns is null || patterns.Count == 0)
        {
            return true;
        }
        return patterns.Any(p => ScmGlob.Match(p, checkName));
    }

    /// <summary>Cap de tentativas efetivo (conexão → default global).</summary>
    public int EffectiveAutofixMaxAttempts(int globalDefault) =>
        Math.Clamp(AutofixMaxAttempts ?? globalDefault, 1, 10);
}

/// <summary>
/// Glob mínimo para padrões de check-name/repo (<c>*</c> = qualquer
/// sequência, incl. vazia; case-insensitive).
/// </summary>
public static class ScmGlob
{
    /// <summary><c>true</c> quando <paramref name="value"/> casa o padrão.</summary>
    public static bool Match(string pattern, string value)
    {
        if (pattern == "*")
        {
            return true;
        }
        var parts = pattern.Split('*');
        var pos = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
            {
                continue;
            }
            var idx = value.IndexOf(
                part, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0 || (i == 0 && !pattern.StartsWith('*') && idx != 0))
            {
                return false;
            }
            pos = idx + part.Length;
        }
        return pattern.EndsWith('*')
            || parts[^1].Length == 0
            || pos == value.Length;
    }
}

/// <summary>SPEC-140: constantes do auto-review (wire values).</summary>
public static class ReviewDefaults
{
    /// <summary>Revisa todo PR não-draft.</summary>
    public const string TriggerModeAll = "all_prs";

    /// <summary>Só revisa PR com a label <see cref="GateLabel"/>.</summary>
    public const string TriggerModeLabelOnly = "label_only";

    /// <summary>Label que destrava o review no modo <c>label_only</c>.</summary>
    public const string GateLabel = "sandcastle-review";

    /// <summary>Status sempre <c>success</c> — review advisory (default).</summary>
    public const string PassConditionCompleted = "completed";

    /// <summary><c>failure</c> quando há finding <c>critical</c>.</summary>
    public const string PassConditionNoCritical = "no_critical";

    /// <summary><c>failure</c> quando há qualquer finding.</summary>
    public const string PassConditionNoFindings = "no_findings";

    /// <summary>Commit-status context publicado pelo review.</summary>
    public const string StatusContext = "sandcastle/review";
}

/// <summary>PR metadata returned by providers.</summary>
public sealed record ScmPullRequest(
    string Url,
    int Number,
    string State,
    string HeadSha,
    string Title);

/// <summary>
/// SPEC-140 RF-002: um arquivo do diff de um PR (forma unificada).
/// <see cref="Patch"/> é o patch unificado do arquivo (pode ser null em
/// arquivos grandes/binários — provider pode omitir).
/// </summary>
public sealed record ScmPullRequestFile(
    string Path,
    string? PreviousPath,
    string Status,
    int Additions,
    int Deletions,
    string? Patch);

/// <summary>
/// SPEC-140 RF-003: comentário inline ancorado numa linha do lado novo
/// (<c>RIGHT</c>) do diff — <see cref="Line"/> é o número de linha do
/// arquivo NOVO (não posição de diff).
/// </summary>
public sealed record ScmReviewComment(
    string Path,
    int Line,
    string Body);

/// <summary>Resultado do posting de uma review no provider.</summary>
public sealed record ScmReviewResult(
    long? ReviewId,
    string? Url,
    int PostedComments);

/// <summary>Parameters for creating a pull request.</summary>
public sealed record ScmPrCreateRequest(
    string Title,
    string Body,
    string? Base,
    string Head,
    bool Draft,
    IReadOnlyList<string>? Reviewers);

/// <summary>
/// Context extracted from an inbound SCM webhook that should become a
/// conversation start-task (mention routing — SPEC-52 RF-004).
/// </summary>
public sealed record ScmMentionContext(
    ScmKind Kind,
    string Repository,
    string? Branch,
    string? PullRequestUrl,
    int? PullRequestNumber,
    string? CommentAuthor,
    string CommentBody,
    long? InReplyToCommentId,
    string? InstallationId);

/// <summary>
/// SCM write/read boundary — one implementation per <see cref="ScmKind"/>.
/// Implementations authenticate via the connection's by-ref secrets and never
/// log tokens.
/// </summary>
public interface IScmProvider
{
    /// <summary>Provider kind.</summary>
    ScmKind Kind { get; }

    /// <summary>Creates a pull request in <paramref name="repository"/> (owner/repo).</summary>
    Task<ScmPullRequest> CreatePullRequestAsync(
        ScmConnection connection, string repository,
        ScmPrCreateRequest request, CancellationToken ct);

    /// <summary>Fetches a PR by number (null = not found).</summary>
    Task<ScmPullRequest?> GetPullRequestAsync(
        ScmConnection connection, string repository, int number,
        CancellationToken ct);

    /// <summary>Posts an issue/PR comment; returns the comment id.</summary>
    Task<long> CreateIssueCommentAsync(
        ScmConnection connection, string repository, int issueNumber,
        string body, CancellationToken ct);

    /// <summary>Replies inside a review-comment thread (keeps threading).</summary>
    Task<long> ReplyToReviewCommentAsync(
        ScmConnection connection, string repository, int pullRequestNumber,
        long inReplyToCommentId, string body, CancellationToken ct);

    /// <summary>Posts a commit status (<paramref name="state"/>: success|failure|pending|error).</summary>
    Task SetCommitStatusAsync( // NOSONAR S107 — SPEC-104 (decisão aprovada): contrato público upstream-parity; param object mudaria API
        ScmConnection connection, string repository, string sha,
        string state, string context, string? targetUrl,
        string? description, CancellationToken ct);

    /// <summary>
    /// SPEC-53 RF-004: valida credenciais + escopo da connection e devolve
    /// diagnóstico (usuário, versão do servidor, repos acessíveis). Nunca ecoa
    /// segredos.
    /// </summary>
    Task<ScmConnectionDiagnostic> TestConnectionAsync(
        ScmConnection connection, CancellationToken ct);

    /// <summary>
    /// SPEC-140 RF-002: lista os arquivos alterados no PR com patch
    /// unificado (GitHub <c>GET /pulls/{n}/files</c>). Providers sem suporte
    /// lançam <see cref="NotSupportedException"/>.
    /// </summary>
    Task<IReadOnlyList<ScmPullRequestFile>> GetPullRequestFilesAsync(
        ScmConnection connection, string repository, int pullRequestNumber,
        CancellationToken ct);

    /// <summary>
    /// SPEC-140 RF-002: lê um arquivo do repo num ref (regras do repo —
    /// AGENTS.md, CLAUDE.md, .sandcastle/review.md). Null = não existe.
    /// Providers sem contents API lançam <see cref="NotSupportedException"/>.
    /// </summary>
    Task<string?> GetFileContentAsync(
        ScmConnection connection, string repository, string path,
        string? reference, CancellationToken ct);

    /// <summary>
    /// SPEC-140 RF-002: lista os paths (relativos ao repo) de um diretório
    /// num ref — usado p/ <c>.sandcastle/rules/</c>. Vazio = dir inexistente.
    /// Providers sem contents API lançam <see cref="NotSupportedException"/>.
    /// </summary>
    Task<IReadOnlyList<string>> ListDirectoryAsync(
        ScmConnection connection, string repository, string directoryPath,
        string? reference, CancellationToken ct);

    /// <summary>
    /// SPEC-140 RF-003: cria uma review no PR com comentários inline
    /// ancorados em linhas do diff (GitHub <c>POST /pulls/{n}/reviews</c>,
    /// event=<c>COMMENT</c>). Retorna null quando o provider não suporta
    /// reviews ancoradas — o caller faz fallback para comentário único.
    /// </summary>
    Task<ScmReviewResult?> CreateReviewAsync(
        ScmConnection connection, string repository, int pullRequestNumber,
        string headSha, string body,
        IReadOnlyList<ScmReviewComment> comments, CancellationToken ct);

    /// <summary>
    /// SPEC-141 RF-001: lista os check runs de um ref (GitHub
    /// <c>GET /commits/{ref}/check-runs</c>) — poll de resultado pós-push
    /// e associação check_suite → check_run. Providers sem Checks API
    /// lançam <see cref="NotSupportedException"/>.
    /// </summary>
    Task<IReadOnlyList<ScmCheckRun>> GetCheckRunsAsync(
        ScmConnection connection, string repository, string reference,
        CancellationToken ct);

    /// <summary>
    /// SPEC-141 RF-003: texto de diagnóstico de um check run
    /// (GitHub: <c>output.title + output.summary + output.text</c> do
    /// check; pode conter truncamento do provider). Null quando vazio.
    /// Providers sem Checks API lançam <see cref="NotSupportedException"/>.
    /// </summary>
    Task<string?> GetCheckRunLogsAsync(
        ScmConnection connection, string repository, long checkRunId,
        CancellationToken ct);

    /// <summary>
    /// SPEC-141 RF-003: logs de uma workflow run (GitHub Actions
    /// <c>GET /actions/runs/{id}/logs</c> — zip de job logs concatenado).
    /// Null quando indisponível. Providers sem Actions API lançam
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    Task<string?> GetWorkflowRunLogsAsync(
        ScmConnection connection, string repository, long workflowRunId,
        CancellationToken ct);

    /// <summary>
    /// SPEC-141 RF-001: PRs associados a um commit (GitHub
    /// <c>GET /commits/{sha}/pulls</c>) — resolução sha → PR quando o
    /// payload não traz <c>pull_requests[]</c>.
    /// Providers sem suporte lançam <see cref="NotSupportedException"/>.
    /// </summary>
    Task<IReadOnlyList<ScmCommitPullRequest>> GetPullRequestsForCommitAsync(
        ScmConnection connection, string repository, string sha,
        CancellationToken ct);
}

/// <summary>Saída de <c>POST /scm/connections/{id}/test</c> (SPEC-53).</summary>
public sealed record ScmConnectionDiagnostic(
    bool Ok,
    string? User,
    string? ServerVersion,
    int? RepoCount,
    string? Detail);

/// <summary>Keyed provider lookup (SPEC-52 RF-001).</summary>
public interface IScmProviderRegistry
{
    /// <summary>Provider for <paramref name="kind"/> (null = not registered).</summary>
    IScmProvider? Get(ScmKind kind);
}

/// <summary>Admin CRUD over <c>scm_connections</c> + GitHub App manifest flow.</summary>
public interface IScmConnectionService
{
    /// <summary>GitHub App manifest JSON for the manifest-conversion flow.</summary>
    object BuildGitHubAppManifest(string redirectUri, string appName);

    /// <summary>
    /// Exchanges the manifest-flow <paramref name="code"/> for app credentials
    /// and persists the connection for the org.
    /// </summary>
    Task<ScmConnection?> CompleteGitHubManifestFlowAsync(
        Guid orgId, string code, string? createdByUserId, CancellationToken ct);

    /// <summary>Registers a connection manually (BYO app credentials).</summary>
    Task<ScmConnection> CreateAsync( // NOSONAR S107 — SPEC-104 (decisão aprovada): contrato público upstream-parity; param object mudaria API
        Guid orgId, ScmKind kind, string? installationId,
        string? pemSecretRef, string? webhookSecretRef,
        IReadOnlyList<string>? repos, string? appId, string? appSlug,
        string? createdByUserId, CancellationToken ct,
        string? apiUrl = null, string? authKind = null,
        string? metaJson = null);

    /// <summary>SPEC-53 RF-004: diagnóstico <c>POST /scm/connections/{id}/test</c>.</summary>
    Task<ScmConnectionDiagnostic> TestAsync(Guid id, CancellationToken ct);

    /// <summary>Lists connections of the org.</summary>
    Task<IReadOnlyList<ScmConnection>> ListAsync(Guid orgId, CancellationToken ct);

    /// <summary>Gets one connection (null = not found).</summary>
    Task<ScmConnection?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Resolves the connection covering <paramref name="repository"/> in the org.</summary>
    Task<ScmConnection?> FindForRepositoryAsync(
        Guid orgId, ScmKind kind, string repository, CancellationToken ct);

    /// <summary>
    /// SPEC-140 RF-001: atualiza o enrollment de auto-review da conexão
    /// (<c>PATCH /scm/connections/{id}</c> — campos <c>review_*</c>;
    /// null = não altera).
    /// </summary>
    Task<ScmConnection?> UpdateReviewSettingsAsync(
        Guid orgId, Guid id, ScmConnectionReviewUpdate update,
        CancellationToken ct);

    /// <summary>
    /// SPEC-141 RF-001: atualiza o enrollment de CI autofix da conexão
    /// (<c>PATCH /scm/connections/{id}</c> — campos <c>autofix_*</c>;
    /// null = não altera).
    /// </summary>
    Task<ScmConnection?> UpdateAutofixSettingsAsync(
        Guid orgId, Guid id, ScmConnectionAutofixUpdate update,
        CancellationToken ct);

    /// <summary>Deletes a connection (false = not found).</summary>
    Task<bool> DeleteAsync(Guid orgId, Guid id, CancellationToken ct);
}

/// <summary>
/// SPEC-140: patch dos campos de auto-review da conexão — qualquer campo
/// null significa "não alterar" (<paramref name="ReviewRepos"/> lista
/// vazia = todos os repos da conexão).
/// </summary>
public sealed record ScmConnectionReviewUpdate(
    bool? ReviewEnabled,
    IReadOnlyList<string>? ReviewRepos,
    string? ReviewTriggerMode,
    string? ReviewPassCondition);

/// <summary>Result of a webhook delivery.</summary>
public enum ScmWebhookResult
{
    /// <summary>Processed (or already processed — idempotent).</summary>
    Processed,

    /// <summary>Replayed delivery id — skipped, still 200.</summary>
    Duplicate,

    /// <summary>401 — bad signature.</summary>
    InvalidSignature,

    /// <summary>404 — no connection for the repository/installation.</summary>
    NoConnection,

    /// <summary>200 — valid but nothing to do (no mention).</summary>
    Ignored,
}

/// <summary>
/// Inbound SCM webhook pipeline: HMAC verify → idempotency → parse →
/// route mentions to conversation start-tasks (SPEC-52 RF-004).
/// </summary>
public interface IScmWebhookService
{
    /// <summary>Handles one GitHub webhook delivery.</summary>
    Task<ScmWebhookResult> HandleGitHubAsync(
        string? signature256, string eventType, string? deliveryId,
        byte[] payload, CancellationToken ct);

    /// <summary>
    /// SPEC-53 RF-004: pipeline genérico — verificação por kind
    /// (GitLab <c>X-Gitlab-Token</c>, Bitbucket HMAC <c>X-Hub-Signature</c>,
    /// AzDO shared secret) → idempotência → menção → start-task.
    /// </summary>
    Task<ScmWebhookResult> HandleAsync(
        ScmKind kind, string? signatureOrToken, string? eventType,
        string? deliveryId, byte[] payload, CancellationToken ct);
}

/// <summary>Outcome of <see cref="IScmPullRequestService.CreateAsync"/>.</summary>
public enum ScmPrError
{
    /// <summary>404 — conversation missing.</summary>
    NotFound,

    /// <summary>404 — no SCM connection covers the repository.</summary>
    NoConnection,

    /// <summary>409 — a PR already exists for this conversation.</summary>
    AlreadyExists,

    /// <summary>502 — push or provider call failed.</summary>
    ProviderError,
}

/// <summary>Result wrapper.</summary>
public sealed record ScmPrResult(ScmPullRequest? PullRequest, ScmPrError? Error)
{
    /// <summary>Success.</summary>
    public static ScmPrResult Ok(ScmPullRequest pr) => new(pr, null);

    /// <summary>Failure.</summary>
    public static ScmPrResult Fail(ScmPrError e) => new(null, e);
}

/// <summary>
/// Creates a PR for a conversation's work branch (push + provider call +
/// persistence + stream event — SPEC-52 RF-003).
/// </summary>
public interface IScmPullRequestService
{
    /// <summary>Pushes the head branch and opens the PR.</summary>
    Task<ScmPrResult> CreateAsync(
        Guid conversationId, ScmPrCreateRequest request,
        string? actorUserId, CancellationToken ct);
}

/// <summary>
/// Reports run completion back to the SCM as a commit status
/// (<c>sandcastle/run</c>) when the conversation has an open PR
/// (SPEC-52 RF-005). Fire-and-forget — failures are logged, never thrown.
/// </summary>
public interface IScmStatusReporter
{
    /// <summary>Posts the status for the conversation's PR head SHA.</summary>
    Task ReportRunFinishedAsync(
        Guid conversationId, bool success, CancellationToken ct);
}