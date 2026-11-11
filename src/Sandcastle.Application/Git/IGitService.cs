using DomainSandbox = Sandcastle.Domain.Sandbox.Sandbox;

namespace Sandcastle.Application.Git;

/// <summary>
/// Git boundary over <c>ISandboxExecutor.ExecAsync</c> — the repo lives inside
/// the sandbox workspace (<c>/workspace/repo</c>); nothing git-related runs on
/// the host beyond the optional host-mode clone (SPEC-12 RF-001).
/// </summary>
public interface IGitService
{
    /// <summary>
    /// Whether <paramref name="repoPath"/> (default <c>/workspace/repo</c>)
    /// contains a git repository.
    /// </summary>
    Task<bool> HasRepositoryAsync(
        DomainSandbox sandbox, CancellationToken ct,
        string? repoPath = null);

    /// <summary>
    /// Clones <paramref name="repoUrl"/> into <paramref name="repoPath"/>
    /// (default <c>/workspace/repo</c>) and creates the work branch.
    /// SPEC-146: <paramref name="credentialToken"/> é o token resolvido da
    /// conexão SCM/credencial do repo — quando null, resolve via
    /// <c>GitCredentialProvider</c> do <paramref name="ownerId"/>.
    /// </summary>
    Task CloneAsync(
        DomainSandbox sandbox, string repoUrl, string workBranch,
        string? ownerId, CancellationToken ct,
        string? repoPath = null, string? credentialToken = null);

    /// <summary>
    /// SPEC-15 RF-004: provisiona autenticação git/gh persistente no sandbox do
    /// <paramref name="ownerId"/> — <c>~/.config/gh/hosts.yml</c> (0600) +
    /// <c>git config --global credential.helper '!gh auth git-credential'</c> —
    /// para que <c>gh</c>, <c>git push</c> e afins funcionem depois do clone.
    /// No-op quando não há token para <c>github.com</c>.
    /// </summary>
    Task<bool> ProvisionGhAuthAsync(
        DomainSandbox sandbox, string? ownerId, CancellationToken ct);

    /// <summary>
    /// SPEC-141 RF-003: provisiona autenticação git/gh no sandbox a partir
    /// de um token JÁ resolvido (ex.: installation token da GitHub App da
    /// conexão SCM) — mesmo layout de <see cref="ProvisionGhAuthAsync"/>,
    /// sem consultar o store de credenciais do usuário. O token nunca é
    /// logado nem persistido.
    /// </summary>
    Task<bool> ProvisionTokenAuthAsync(
        DomainSandbox sandbox, string host, string token,
        CancellationToken ct);

    /// <summary>Structured status buckets (porcelain v2).</summary>
    Task<GitChanges> GetChangesAsync(
        DomainSandbox sandbox, CancellationToken ct, string? repoPath = null);

    /// <summary>Structured diff vs HEAD (unified, parsed).</summary>
    Task<GitDiffReport?> GetDiffAsync(
        DomainSandbox sandbox, string? path, CancellationToken ct,
        string? repoPath = null);

    /// <summary>Raw unified diff text vs HEAD.</summary>
    Task<string?> GetRawDiffAsync(
        DomainSandbox sandbox, string? path, CancellationToken ct,
        string? repoPath = null);

    /// <summary>Commit log (newest first, bounded).</summary>
    Task<IReadOnlyList<GitLogEntry>> GetLogAsync(
        DomainSandbox sandbox, int limit, CancellationToken ct,
        string? repoPath = null);

    /// <summary>
    /// SPEC-21 RF-006: conteúdo de <paramref name="path"/> na revisão
    /// <paramref name="gitRef"/> (default HEAD) — para o diff editor do
    /// client. Null quando o arquivo não existe naquela revisão.
    /// </summary>
    Task<string?> GetFileAtRefAsync(
        DomainSandbox sandbox, string path, string gitRef,
        CancellationToken ct, string? repoPath = null);

    /// <summary>Stages <paramref name="files"/> (or all) and commits as the agent author.</summary>
    Task<GitCommitResult> CommitAsync(
        DomainSandbox sandbox, string message,
        IReadOnlyList<string>? files, CancellationToken ct,
        string? repoPath = null);

    /// <summary>
    /// SPEC-52 RF-003: pushes <paramref name="head"/> to <c>origin</c>
    /// (<paramref name="remoteBranch"/> default = head). Credentials come
    /// from the provisioned <c>gh</c>/askpass helpers — never embedded in
    /// URLs. Returns the head SHA on success.
    /// </summary>
    Task<GitPushResult> PushAsync(
        DomainSandbox sandbox, string head, string? remoteBranch,
        bool forceWithLease, string? ownerId, CancellationToken ct,
        string? repoPath = null);

    /// <summary>Current work branch name (null = detached/no repo).</summary>
    Task<string?> GetCurrentBranchAsync(
        DomainSandbox sandbox, CancellationToken ct, string? repoPath = null);
}