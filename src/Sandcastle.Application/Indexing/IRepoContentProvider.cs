namespace Sandcastle.Application.Indexing;

/// <summary>
/// Parameters for a repo content acquisition (SPEC-145 RF-001).
/// </summary>
public sealed record RepoContentRequest(
    string Repository,
    string Branch,
    Guid? OrgId);

/// <summary>
/// A checked-out view of a repository at a resolved commit. Implementations
/// own the lifetime of <see cref="RootPath"/> — disposing the lease may
/// delete the workdir, so consumers must finish file IO inside the lease.
/// </summary>
public interface IRepoContentLease : IAsyncDisposable
{
    /// <summary>Root directory containing the repo working tree.</summary>
    string RootPath { get; }

    /// <summary>Resolved HEAD commit sha (null when not a git checkout).</summary>
    string? CommitSha { get; }

    /// <summary>Provider kind that produced the lease (local|git).</summary>
    string ProviderKind { get; }
}

/// <summary>
/// Repo content source for indexing/wiki generation (SPEC-145 RF-001):
/// either a shallow git clone of the remote repo (via the org SCM
/// connection credential) or the local workspace checkout (dev repo).
/// </summary>
public interface IRepoContentProvider
{
    /// <summary>
    /// Acquires a workdir with the repository content at
    /// <paramref name="request"/>.Branch. Implementations that cannot
    /// serve the repository return null (the resolver falls through).
    /// </summary>
    Task<IRepoContentLease?> AcquireAsync(
        RepoContentRequest request, CancellationToken ct);
}