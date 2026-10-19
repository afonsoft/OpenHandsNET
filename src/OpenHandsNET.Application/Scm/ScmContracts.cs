namespace OpenHandsNET.Application.Scm;

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
    string? MetaJson = null);

/// <summary>PR metadata returned by providers.</summary>
public sealed record ScmPullRequest(
    string Url,
    int Number,
    string State,
    string HeadSha,
    string Title);

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
    Task SetCommitStatusAsync(
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
    Task<ScmConnection> CreateAsync(
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

    /// <summary>Deletes a connection (false = not found).</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}

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
/// (<c>openhandsnet/run</c>) when the conversation has an open PR
/// (SPEC-52 RF-005). Fire-and-forget — failures are logged, never thrown.
/// </summary>
public interface IScmStatusReporter
{
    /// <summary>Posts the status for the conversation's PR head SHA.</summary>
    Task ReportRunFinishedAsync(
        Guid conversationId, bool success, CancellationToken ct);
}