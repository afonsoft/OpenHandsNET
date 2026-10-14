using DomainSandbox = OpenHandsNET.Domain.Sandbox.Sandbox;

namespace OpenHandsNET.Application.Git;

/// <summary>
/// Git boundary over <c>ISandboxExecutor.ExecAsync</c> — the repo lives inside
/// the sandbox workspace (<c>/workspace/repo</c>); nothing git-related runs on
/// the host beyond the optional host-mode clone (SPEC-12 RF-001).
/// </summary>
public interface IGitService
{
    /// <summary>Whether <c>/workspace/repo</c> contains a git repository.</summary>
    Task<bool> HasRepositoryAsync(DomainSandbox sandbox, CancellationToken ct);

    /// <summary>Clones <paramref name="repoUrl"/> into <c>/workspace/repo</c> and creates the work branch.</summary>
    Task CloneAsync(
        DomainSandbox sandbox, string repoUrl, string workBranch,
        string? ownerId, CancellationToken ct);

    /// <summary>
    /// SPEC-15 RF-004: provisiona autenticação git/gh persistente no sandbox do
    /// <paramref name="ownerId"/> — <c>~/.config/gh/hosts.yml</c> (0600) +
    /// <c>git config --global credential.helper '!gh auth git-credential'</c> —
    /// para que <c>gh</c>, <c>git push</c> e afins funcionem depois do clone.
    /// No-op quando não há token para <c>github.com</c>.
    /// </summary>
    Task<bool> ProvisionGhAuthAsync(
        DomainSandbox sandbox, string? ownerId, CancellationToken ct);

    /// <summary>Structured status buckets (porcelain v2).</summary>
    Task<GitChanges> GetChangesAsync(DomainSandbox sandbox, CancellationToken ct);

    /// <summary>Structured diff vs HEAD (unified, parsed).</summary>
    Task<GitDiffReport?> GetDiffAsync(
        DomainSandbox sandbox, string? path, CancellationToken ct);

    /// <summary>Raw unified diff text vs HEAD.</summary>
    Task<string?> GetRawDiffAsync(
        DomainSandbox sandbox, string? path, CancellationToken ct);

    /// <summary>Commit log (newest first, bounded).</summary>
    Task<IReadOnlyList<GitLogEntry>> GetLogAsync(
        DomainSandbox sandbox, int limit, CancellationToken ct);

    /// <summary>
    /// SPEC-21 RF-006: conteúdo de <paramref name="path"/> na revisão
    /// <paramref name="gitRef"/> (default HEAD) — para o diff editor do
    /// client. Null quando o arquivo não existe naquela revisão.
    /// </summary>
    Task<string?> GetFileAtRefAsync(
        DomainSandbox sandbox, string path, string gitRef,
        CancellationToken ct);

    /// <summary>Stages <paramref name="files"/> (or all) and commits as the agent author.</summary>
    Task<GitCommitResult> CommitAsync(
        DomainSandbox sandbox, string message,
        IReadOnlyList<string>? files, CancellationToken ct);
}
