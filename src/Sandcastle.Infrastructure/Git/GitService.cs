using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Git;
using Sandcastle.Application.Sandbox;
using Sandcastle.Infrastructure.Agent;

using DomainSandbox = Sandcastle.Domain.Sandbox.Sandbox;

namespace Sandcastle.Infrastructure.Git;

/// <summary>
/// <see cref="IGitService"/> implementation: every operation is a
/// <c>git</c> CLI exec inside the sandbox container — the repo lives at
/// <c>/workspace/repo</c> (SPEC-12 invariants).
/// </summary>
public sealed class GitService(
    ISandboxExecutor executor,
    GitCredentialProvider credentials,
    IOptions<GitOptions> options,
    GitAuthorResolver authors,
    ILogger<GitService> logger) : IGitService
{
    /// <inheritdoc />
    public async Task<bool> HasRepositoryAsync(
        DomainSandbox sandbox, CancellationToken ct, string? repoPath = null)
    {
        var repo = PathFor(repoPath);
        var r = await executor.ExecAsync(sandbox,
            $"git -C '{repo}' rev-parse --is-inside-work-tree",
            TimeSpan.FromSeconds(15), ct);
        return r.StdOut.Trim() == "true";
    }

    /// <inheritdoc />
    public async Task CloneAsync(
        DomainSandbox sandbox, string repoUrl, string workBranch,
        string? ownerId, CancellationToken ct,
        string? repoPath = null, string? credentialToken = null)
    {
        var repo = PathFor(repoPath);
        // SPEC-18 RF-005: recriação de sandbox (switch_profile) monta o mesmo
        // workspace do host — repo já clonado → só garante o work branch.
        if (await HasRepositoryAsync(sandbox, ct, repo))
        {
            var sw = await executor.ExecAsync(sandbox,
                $"git -C '{repo}' checkout '{Escape(workBranch)}' 2>/dev/null || " +
                $"git -C '{repo}' checkout -b '{Escape(workBranch)}'",
                TimeSpan.FromSeconds(30), ct);
            if (sw.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"work branch failed: {sw.StdErr.Trim()}");
            }

            return;
        }

        var url = NormalizeUrl(repoUrl);
        // URL com credencial embutida nunca chega a logs — redação defensiva.
        var safeUrl = SecretRedactor.Redact(url);
        var host = TryHost(url);
        var token = credentialToken ?? (host is null ? null
            : await credentials.ResolveTokenAsync(host, ownerId, ct));

        string askPassPath = "";
        try
        {
            string envPrefix = "";
            if (token is not null)
            {
                askPassPath =
                    $"/var/lib/sc-secrets/.sc-askpass-{Guid.NewGuid():N}";
                await executor.WriteFileAsync(sandbox, askPassPath,
                    GitCredentialProvider.AskPassScript(token), ct);
                await executor.ExecAsync(sandbox, $"chmod 700 '{askPassPath}'",
                    TimeSpan.FromSeconds(10), ct);
                envPrefix =
                    $"GIT_ASKPASS='{askPassPath}' GIT_TERMINAL_PROMPT=0 ";
            }

            logger.LogInformation("Cloning {RepoUrl} into {RepoPath} (container mode)",
                safeUrl, repo);
            var clone = await executor.ExecAsync(sandbox,
                $"{envPrefix}git clone -- '{Escape(url)}' '{repo}'",
                TimeSpan.FromMinutes(10), ct);
            if (clone.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git clone failed: {SecretRedactor.Redact(clone.StdErr.Trim())}");
            }

            var co = await executor.ExecAsync(sandbox,
                $"git -C '{repo}' checkout -b '{Escape(workBranch)}'",
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
    public async Task<bool> ProvisionGhAuthAsync(
        DomainSandbox sandbox, string? ownerId, CancellationToken ct)
    {
        var token = await credentials.ResolveTokenAsync(
            "github.com", ownerId, ct);
        if (token is null)
        {
            return false;
        }

        return await ProvisionTokenAuthAsync(sandbox, "github.com", token, ct);
    }

    /// <inheritdoc />
    public async Task<bool> ProvisionTokenAuthAsync(
        DomainSandbox sandbox, string host, string token,
        CancellationToken ct)
    {
        var hostsB64 = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(
                GitCredentialProvider.GhHostsFile(host, token)));
        // hosts.yml autentica gh mesmo sem GH_TOKEN no env (ex.: warm pool);
        // credential.helper faz git https usar o gh como askpass.
        var r = await executor.ExecAsync(sandbox,
            "d=\"$HOME/.config/gh\" && mkdir -p \"$d\" && " +
            $"echo '{hostsB64}' | base64 -d > \"$d/hosts.yml\" && " +
            "chmod 600 \"$d/hosts.yml\" && " +
            "git config --global credential.helper '!gh auth git-credential'",
            TimeSpan.FromSeconds(30), ct);
        if (r.ExitCode != 0)
        {
            logger.LogWarning("gh/git auth provision failed in sandbox {Id}: {Err}",
                sandbox.Id, SecretRedactor.Redact(r.StdErr.Trim()));
            return false;
        }

        logger.LogInformation(
            "gh/git credentials provisioned in sandbox {Id} (host {Host})",
            sandbox.Id, host);
        return true;
    }

    /// <inheritdoc />
    public async Task<GitChanges> GetChangesAsync(
        DomainSandbox sandbox, CancellationToken ct, string? repoPath = null)
    {
        var repo = PathFor(repoPath);
        var r = await executor.ExecAsync(sandbox,
            $"git -C '{repo}' status --porcelain=v2 --branch -uall",
            TimeSpan.FromSeconds(30), ct);
        return r.ExitCode == 0
            ? GitStatusParser.Parse(r.StdOut)
            : throw new InvalidOperationException(
                $"git status failed: {r.StdErr.Trim()}");
    }

    /// <inheritdoc />
    public async Task<GitDiffReport?> GetDiffAsync(
        DomainSandbox sandbox, string? path, CancellationToken ct,
        string? repoPath = null)
    {
        var raw = await GetRawDiffAsync(sandbox, path, ct, repoPath);
        return raw is null ? null : GitDiffParser.Parse(raw);
    }

    /// <inheritdoc />
    public async Task<string?> GetRawDiffAsync(
        DomainSandbox sandbox, string? path, CancellationToken ct,
        string? repoPath = null)
    {
        var repo = PathFor(repoPath);
        if (!await HasRepositoryAsync(sandbox, ct, repo))
        {
            return null;
        }

        var pathArg = path is null ? "" : $" -- '{Escape(path)}'";
        var r = await executor.ExecAsync(sandbox,
            $"git -C '{repo}' diff HEAD{pathArg} || " +
            $"git -C '{repo}' diff{pathArg}",
            TimeSpan.FromSeconds(30), ct);
        return r.StdOut;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitLogEntry>> GetLogAsync(
        DomainSandbox sandbox, int limit, CancellationToken ct,
        string? repoPath = null)
    {
        var repo = PathFor(repoPath);
        var r = await executor.ExecAsync(sandbox,
            $"git -C '{repo}' log -n {Math.Clamp(limit, 1, 200)} " +
            "--format=%H%x1f%an%x1f%aI%x1f%s%x1e",
            TimeSpan.FromSeconds(30), ct);
        return r.ExitCode == 0 ? GitLogParser.Parse(r.StdOut) : [];
    }

    /// <inheritdoc />
    public async Task<GitCommitResult> CommitAsync(
        DomainSandbox sandbox, string message,
        IReadOnlyList<string>? files, CancellationToken ct,
        string? repoPath = null)
    {
        var repo = PathFor(repoPath);
        var add = files is { Count: > 0 }
            ? string.Join(' ', files.Select(f => $"'{Escape(f)}'"))
            : "-A";
        // SPEC-147 RF-002: autoria resolvida user→org→global +
        // trailer Co-authored-by do dono da conversa.
        var identity = await authors.ResolveForConversationAsync(
            sandbox.ConversationId, ct);
        var fullMessage = identity.CoAuthorTrailer is { } trailer
            && !message.Contains("Co-authored-by:", StringComparison.Ordinal)
            ? $"{message}\n\n{trailer}"
            : message;
        var msgB64 = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(fullMessage));
        var r = await executor.ExecAsync(sandbox,
            $"cd '{repo}' && git add {add} && " +
            $"git -c user.name='{Escape(identity.AuthorName)}' " +
            $"-c user.email='{Escape(identity.AuthorEmail)}' " +
            $"commit -m \"$(echo '{msgB64}' | base64 -d)\" && git rev-parse HEAD",
            TimeSpan.FromSeconds(60), ct);
        var hash = r.StdOut.Trim().Split('\n').LastOrDefault();
        return new GitCommitResult(
            r.ExitCode == 0,
            r.ExitCode == 0 ? hash : null,
            r.StdOut, r.ExitCode == 0 ? null : r.StdErr);
    }

    /// <inheritdoc />
    public async Task<GitPushResult> PushAsync(
        DomainSandbox sandbox, string head, string? remoteBranch,
        bool forceWithLease, string? ownerId, CancellationToken ct,
        string? repoPath = null)
    {
        var repo = PathFor(repoPath);
        if (!await HasRepositoryAsync(sandbox, ct, repo))
        {
            return new GitPushResult(false, null, "no git repository");
        }

        // Garante credencial gh/askpass quando disponível (SPEC-15 RF-004).
        await ProvisionGhAuthAsync(sandbox, ownerId, ct);

        var dest = remoteBranch ?? head;
        var force = forceWithLease ? " --force-with-lease" : "";
        var r = await executor.ExecAsync(sandbox,
            $"cd '{repo}' && git push -u origin " +
            $"'{Escape(head)}:{Escape(dest)}'{force} && git rev-parse HEAD",
            TimeSpan.FromMinutes(5), ct);
        if (r.ExitCode != 0)
        {
            return new GitPushResult(false, null,
                SecretRedactor.Redact(r.StdErr.Trim()));
        }

        return new GitPushResult(true, r.StdOut.Trim().Split('\n')[^1], null);
    }

    /// <inheritdoc />
    public async Task<string?> GetCurrentBranchAsync(
        DomainSandbox sandbox, CancellationToken ct, string? repoPath = null)
    {
        var repo = PathFor(repoPath);
        var r = await executor.ExecAsync(sandbox,
            $"git -C '{repo}' rev-parse --abbrev-ref HEAD",
            TimeSpan.FromSeconds(15), ct);
        var name = r.StdOut.Trim();
        return r.ExitCode == 0 && name is not "" and not "HEAD" ? name : null;
    }

    /// <inheritdoc />
    public async Task<string?> GetFileAtRefAsync(
        DomainSandbox sandbox, string path, string gitRef,
        CancellationToken ct, string? repoPath = null)
    {
        var repo = PathFor(repoPath);
        // path relativo ao repo (cliente envia /workspace/... — faz strip).
        var rel = path.StartsWith(repo, StringComparison.Ordinal)
            ? path[repo.Length..].TrimStart('/')
            : path.TrimStart('/');
        if (rel.Length == 0 || rel.Contains("..", StringComparison.Ordinal)
            || gitRef.Contains('\'', StringComparison.Ordinal))
        {
            return null;
        }

        var r = await executor.ExecAsync(sandbox,
            $"git -C '{repo}' show '{Escape(gitRef)}:{Escape(rel)}'",
            TimeSpan.FromSeconds(30), ct);
        return r.ExitCode == 0 ? r.StdOut : null;
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

    /// <summary>
    /// SPEC-146: <paramref name="repoPath"/> override — default
    /// <see cref="GitOptions.RepoPath"/> (<c>/workspace/repo</c> = primário).
    /// Só caminhos sob <c>/workspace</c> são aceitos (guardrail de escape).
    /// </summary>
    private string PathFor(string? repoPath) =>
        string.IsNullOrEmpty(repoPath)
            || !repoPath.StartsWith("/workspace/", StringComparison.Ordinal)
            ? Repo : repoPath;

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