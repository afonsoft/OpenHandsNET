using System.Diagnostics;

using Microsoft.Extensions.Options;

using Sandcastle.Application.Indexing;

namespace Sandcastle.Infrastructure.Indexing;

/// <summary>
/// Local workdir lease — nothing to dispose.
/// </summary>
public sealed class RepoContentLease(
    string rootPath, string? commitSha, string providerKind)
    : IRepoContentLease
{
    /// <inheritdoc />
    public string RootPath { get; } = rootPath;

    /// <inheritdoc />
    public string? CommitSha { get; } = commitSha;

    /// <inheritdoc />
    public string ProviderKind { get; } = providerKind;

    /// <summary>Temp dir to delete on dispose (null = keep).</summary>
    public string? CleanupPath { get; init; }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (CleanupPath is not null)
        {
            try
            {
                Directory.Delete(CleanupPath, recursive: true);
            }
            catch (Exception)
            {
                // best-effort — SO reclaims temp files eventually.
            }
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// <see cref="IRepoContentProvider"/> sobre o checkout local do servidor
/// (dev repo — o caso coberto hoje). Resolve o HEAD via
/// <c>git rev-parse HEAD</c> quando o diretório é um work-tree git.
/// </summary>
public sealed class LocalRepoContentProvider(
    IOptions<IndexingOptions> options) : IRepoContentProvider
{
    /// <inheritdoc />
    public async Task<IRepoContentLease?> AcquireAsync(
        RepoContentRequest request, CancellationToken ct)
    {
        var root = options.Value.LocalRepoPath
            ?? Directory.GetCurrentDirectory();
        if (!Directory.Exists(root))
        {
            return null;
        }

        var sha = await GitHeadAsync(root, options.Value.GitPath, ct);
        return new RepoContentLease(root, sha, "local");
    }

    /// <summary>HEAD do work-tree (null quando não é repo git / git ausente).</summary>
    internal static async Task<string?> GitHeadAsync(
        string root, string gitPath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = gitPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(root);
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("HEAD");
            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var sha = stdout.Trim();
            return p.ExitCode == 0 && sha.Length == 40 ? sha : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}