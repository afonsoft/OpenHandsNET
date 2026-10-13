using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Git;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Infrastructure.Agent;
using DomainSandbox = OpenHandsNET.Domain.Sandbox.Sandbox;

namespace OpenHandsNET.Infrastructure.Git;

/// <summary>
/// <see cref="IGitService"/> implementation: every operation is a
/// <c>git</c> CLI exec inside the sandbox container — the repo lives at
/// <c>/workspace/repo</c> (SPEC-12 invariants).
/// </summary>
public sealed class GitService(
    ISandboxExecutor executor,
    GitCredentialProvider credentials,
    IOptions<GitOptions> options,
    ILogger<GitService> logger) : IGitService
{
    /// <inheritdoc />
    public async Task<bool> HasRepositoryAsync(DomainSandbox sandbox, CancellationToken ct)
    {
        var r = await executor.ExecAsync(sandbox,
            $"git -C {Repo} rev-parse --is-inside-work-tree",
            TimeSpan.FromSeconds(15), ct);
        return r.StdOut.Trim() == "true";
    }

    /// <inheritdoc />
    public async Task CloneAsync(
        DomainSandbox sandbox, string repoUrl, string workBranch, CancellationToken ct)
    {
        var url = NormalizeUrl(repoUrl);
        // URL com credencial embutida nunca chega a logs — redação defensiva.
        var safeUrl = SecretRedactor.Redact(url);
        var host = TryHost(url);
        var token = host is null ? null : await credentials.ResolveTokenAsync(host, ct);

        string askPassPath = "";
        try
        {
            string envPrefix = "";
            if (token is not null)
            {
                askPassPath = $"/tmp/.ohn-askpass-{Guid.NewGuid():N}";
                await executor.WriteFileAsync(sandbox, askPassPath,
                    GitCredentialProvider.AskPassScript(token), ct);
                await executor.ExecAsync(sandbox, $"chmod 700 '{askPassPath}'",
                    TimeSpan.FromSeconds(10), ct);
                envPrefix =
                    $"GIT_ASKPASS='{askPassPath}' GIT_TERMINAL_PROMPT=0 ";
            }

            logger.LogInformation("Cloning {Repo} into {Path} (container mode)",
                safeUrl, Repo);
            var clone = await executor.ExecAsync(sandbox,
                $"{envPrefix}git clone -- '{Escape(url)}' '{Repo}'",
                TimeSpan.FromMinutes(10), ct);
            if (clone.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git clone failed: {SecretRedactor.Redact(clone.StdErr.Trim())}");
            }

            var co = await executor.ExecAsync(sandbox,
                $"git -C '{Repo}' checkout -b '{Escape(workBranch)}'",
                TimeSpan.FromSeconds(30), ct);
            if (co.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"work branch failed: {co.StdErr.Trim()}");
            }
        }
        finally
        {
            if (askPassPath.Length > 0)
            {
                await executor.ExecAsync(sandbox, $"rm -f '{askPassPath}'",
                    TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    /// <inheritdoc />
    public async Task<GitChanges> GetChangesAsync(
        DomainSandbox sandbox, CancellationToken ct)
    {
        var r = await executor.ExecAsync(sandbox,
            $"git -C '{Repo}' status --porcelain=v2 --branch -uall",
            TimeSpan.FromSeconds(30), ct);
        return r.ExitCode == 0
            ? GitStatusParser.Parse(r.StdOut)
            : throw new InvalidOperationException(
                $"git status failed: {r.StdErr.Trim()}");
    }

    /// <inheritdoc />
    public async Task<GitDiffReport?> GetDiffAsync(
        DomainSandbox sandbox, string? path, CancellationToken ct)
    {
        var raw = await GetRawDiffAsync(sandbox, path, ct);
        return raw is null ? null : GitDiffParser.Parse(raw);
    }

    /// <inheritdoc />
    public async Task<string?> GetRawDiffAsync(
        DomainSandbox sandbox, string? path, CancellationToken ct)
    {
        if (!await HasRepositoryAsync(sandbox, ct))
        {
            return null;
        }

        var pathArg = path is null ? "" : $" -- '{Escape(path)}'";
        var r = await executor.ExecAsync(sandbox,
            $"git -C '{Repo}' diff HEAD{pathArg} || git -C '{Repo}' diff{pathArg}",
            TimeSpan.FromSeconds(30), ct);
        return r.StdOut;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitLogEntry>> GetLogAsync(
        DomainSandbox sandbox, int limit, CancellationToken ct)
    {
        var r = await executor.ExecAsync(sandbox,
            $"git -C '{Repo}' log -n {Math.Clamp(limit, 1, 200)} " +
            "--format=%H%x1f%an%x1f%aI%x1f%s%x1e",
            TimeSpan.FromSeconds(30), ct);
        return r.ExitCode == 0 ? GitLogParser.Parse(r.StdOut) : [];
    }

    /// <inheritdoc />
    public async Task<GitCommitResult> CommitAsync(
        DomainSandbox sandbox, string message,
        IReadOnlyList<string>? files, CancellationToken ct)
    {
        var add = files is { Count: > 0 }
            ? string.Join(' ', files.Select(f => $"'{Escape(f)}'"))
            : "-A";
        var msgB64 = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(message));
        var r = await executor.ExecAsync(sandbox,
            $"cd '{Repo}' && git add {add} && " +
            $"git -c user.name='{options.Value.AuthorName}' " +
            $"-c user.email='{options.Value.AuthorEmail}' " +
            $"commit -m \"$(echo '{msgB64}' | base64 -d)\" && git rev-parse HEAD",
            TimeSpan.FromSeconds(60), ct);
        var hash = r.StdOut.Trim().Split('\n').LastOrDefault();
        return new GitCommitResult(
            r.ExitCode == 0,
            r.ExitCode == 0 ? hash : null,
            r.StdOut, r.ExitCode == 0 ? null : r.StdErr);
    }

    /// <summary><c>owner/repo</c> → provider HTTPS; file:// e URLs passam direto.</summary>
    private static string NormalizeUrl(string repoUrl)
    {
        if (Uri.TryCreate(repoUrl, UriKind.Absolute, out var u) &&
            (u.Scheme is "https" or "http" or "file" or "ssh" or "git"))
        {
            return repoUrl;
        }

        // owner/repo shorthand → github (provider default).
        return repoUrl.Count(c => c == '/') == 1 && !repoUrl.StartsWith('/')
            && !repoUrl.StartsWith("file:", StringComparison.Ordinal)
            ? $"https://github.com/{repoUrl}"
            : repoUrl;
    }

    private string Repo => options.Value.RepoPath;

    private static string? TryHost(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        // owner/repo shorthand → provider default host.
        return url.StartsWith("https://", StringComparison.Ordinal)
            ? url["https://".Length..].Split('/')[0] : null;
    }

    private static string Escape(string s) => s.Replace("'", "'\\''");
}
