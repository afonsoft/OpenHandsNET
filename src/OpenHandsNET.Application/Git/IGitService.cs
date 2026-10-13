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
        DomainSandbox sandbox, string repoUrl, string workBranch, CancellationToken ct);

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

    /// <summary>Stages <paramref name="files"/> (or all) and commits as the agent author.</summary>
    Task<GitCommitResult> CommitAsync(
        DomainSandbox sandbox, string message,
        IReadOnlyList<string>? files, CancellationToken ct);
}
