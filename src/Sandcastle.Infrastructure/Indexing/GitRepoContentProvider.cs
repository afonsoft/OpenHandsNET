using System.Diagnostics;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Contracts;
using Sandcastle.Application.Indexing;
using Sandcastle.Application.Scm;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Scm;

namespace Sandcastle.Infrastructure.Indexing;

/// <summary>
/// <see cref="IRepoContentProvider"/> para repos remotos: resolve a
/// conexão SCM que cobre o repo, obtém a credencial (installation token
/// do GitHub App ou PAT por-referência) e faz
/// <c>git clone --depth N --branch B</c> num workdir temporário — o
/// token nunca vai para argv (GIT_ASKPASS num arquivo chmod 700) nem
/// para logs (<see cref="SecretRedactor"/>).
/// </summary>
public sealed class GitRepoContentProvider : IRepoContentProvider
{
    private readonly AppDbContext _db;
    private readonly IGitHubClientFactory _ghFactory;
    private readonly ISecretStore _secrets;
    private readonly IOptions<IndexingOptions> _options;
    private readonly ILogger<GitRepoContentProvider> _logger;

    /// <summary>Seam de testes: clone URL por conexão+repo (null = default por kind).</summary>
    private readonly Func<ScmConnection, string, string>? _cloneUrlFor;

    /// <summary>Seam de testes: token por conexão (null = resolução por kind).</summary>
    private readonly Func<ScmConnection, CancellationToken, Task<string?>>? _tokenFor;

    public GitRepoContentProvider(
        AppDbContext db,
        IGitHubClientFactory ghFactory,
        ISecretStore secrets,
        IOptions<IndexingOptions> options,
        ILogger<GitRepoContentProvider> logger,
        Func<ScmConnection, string, string>? cloneUrlFor = null,
        Func<ScmConnection, CancellationToken, Task<string?>>? tokenFor = null)
    {
        _db = db;
        _ghFactory = ghFactory;
        _secrets = secrets;
        _options = options;
        _logger = logger;
        _cloneUrlFor = cloneUrlFor;
        _tokenFor = tokenFor;
    }

    /// <inheritdoc />
    public async Task<IRepoContentLease?> AcquireAsync(
        RepoContentRequest request, CancellationToken ct)
    {
        var connection = await FindConnectionAsync(
            request.Repository, request.OrgId, ct);
        if (connection is null)
        {
            return null;
        }

        var o = _options.Value;
        var cloneUrl = _cloneUrlFor?.Invoke(connection, request.Repository)
            ?? DefaultCloneUrl(connection, request.Repository);
        var token = _tokenFor is not null
            ? await _tokenFor(connection, ct)
            : await ResolveTokenAsync(connection, ct);

        var tempRoot = string.IsNullOrEmpty(o.TempRoot)
            ? Path.GetTempPath() : o.TempRoot!;
        var workdir = Path.Combine(tempRoot, $"sc-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workdir);

        string? askPass = null;
        try
        {
            if (token is not null)
            {
                askPass = Path.Combine(workdir, ".git-askpass.sh");
                await File.WriteAllTextAsync(askPass,
                    AskPassScript(UsernameFor(connection.Kind), token), ct);
                try
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(askPass,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite
                            | UnixFileMode.UserExecute);
                    }
                }
                catch (Exception)
                {
                    // best-effort — permissões de askpass não bloqueiam.
                }
            }

            var depth = Math.Max(1, o.CloneDepth);
            var cloneArgs = new List<string> { "clone", "--depth",
                depth.ToString(System.Globalization.CultureInfo
                    .InvariantCulture) };
            if (!string.IsNullOrEmpty(request.Branch))
            {
                cloneArgs.Add("--branch");
                cloneArgs.Add(request.Branch);
                cloneArgs.Add("--single-branch");
            }
            cloneArgs.Add("--");
            cloneArgs.Add(cloneUrl);
            cloneArgs.Add(Path.Combine(workdir, "repo"));

            var result = await RunGitAsync(
                o.GitPath, cloneArgs, askPass, null,
                TimeSpan.FromMinutes(Math.Max(1, o.CloneTimeoutMinutes)), ct);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "git clone failed: "
                    + SecretRedactor.Redact(result.StdErr.Trim()));
            }

            var head = await RunGitAsync(
                o.GitPath,
                ["-C", Path.Combine(workdir, "repo"), "rev-parse", "HEAD"],
                null, null, TimeSpan.FromSeconds(30), ct);
            var sha = head.ExitCode == 0 ? head.StdOut.Trim() : null;
            return new RepoContentLease(
                Path.Combine(workdir, "repo"),
                sha is { Length: 40 } ? sha : null,
                "git")
            { CleanupPath = workdir };
        }
        catch
        {
            try { Directory.Delete(workdir, recursive: true); }
            catch (Exception) { /* best-effort cleanup */ }
            throw;
        }
        finally
        {
            // O askpass morre com o workdir (repo checkout em ./repo e o
            // script em ./) — se o clone falhou o catch já limpou.
            if (askPass is not null && File.Exists(askPass))
            {
                try { File.Delete(askPass); } catch (Exception) { /* noop */ }
            }
        }
    }

    /// <summary>Connection covering the repo (org-scoped quando orgId vem).</summary>
    internal Task<ScmConnection?> FindConnectionAsync(
        string repository, Guid? orgId, CancellationToken ct) =>
        RepoConnectionLookup.FindCoveringAsync(
            _db, repository, orgId, ct);

    /// <summary>Credencial do clone: GitHub App installation token; demais kinds usam o PAT por-referência.</summary>
    internal async Task<string?> ResolveTokenAsync(
        ScmConnection connection, CancellationToken ct)
    {
        try
        {
            if (connection.Kind == ScmKind.GitHub
                && connection.InstallationId is not null
                && connection.AppId is not null
                && connection.PemSecretRef is not null)
            {
                return await _ghFactory.GetInstallationTokenAsync(
                    connection, ct);
            }

            if (connection.PemSecretRef is not null)
            {
                return await _secrets.ResolveAsync(
                    "org", connection.OrgId?.ToString() ?? "",
                    connection.PemSecretRef, ct);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e,
                "clone credential resolution failed (conn {Id})", connection.Id);
        }
        return null;
    }

    /// <summary>Default clone URL por kind (ApiUrl cobre self-hosted).</summary>
    internal static string DefaultCloneUrl(
        ScmConnection connection, string repository)
    {
        var baseUrl = connection.ApiUrl?.TrimEnd('/');
        return connection.Kind switch
        {
            ScmKind.GitHub =>
                $"{baseUrl ?? "https://github.com"}/{repository}.git",
            ScmKind.GitLab =>
                $"{baseUrl ?? "https://gitlab.com"}/{repository}.git",
            ScmKind.Bitbucket =>
                $"{baseUrl ?? "https://bitbucket.org"}/{repository}.git",
            ScmKind.AzureDevOps =>
                $"{baseUrl ?? "https://dev.azure.com"}/{repository}",
            _ => $"{baseUrl ?? "https://github.com"}/{repository}.git",
        };
    }

    /// <summary>Username exigido pelo provider no par user:token.</summary>
    internal static string UsernameFor(ScmKind kind) => kind switch
    {
        ScmKind.GitHub => "x-access-token",
        ScmKind.GitLab => "oauth2",
        ScmKind.Bitbucket => "x-token-auth",
        _ => "token",
    };

    /// <summary>
    /// GIT_ASKPASS script — responde o prompt de Username/Password do git
    /// sem expor o token em argv nem em env vars visíveis no processo.
    /// </summary>
    internal static string AskPassScript(string username, string token) =>
        "#!/bin/sh\n" +
        "case \"$1\" in\n" +
        $"  *Username*) printf '%s\\n' '{username}' ;;\n" +
        $"  *) printf '%s\\n' '{token.Replace("'", "'\\''",
            StringComparison.Ordinal)}' ;;\n" +
        "esac\n";

    /// <summary>Process exec com git — env vars de askpass quando há token.</summary>
    internal static async Task<(int ExitCode, string StdOut, string StdErr)>
        RunGitAsync(string gitPath, IReadOnlyList<string> arguments,
            string? askPassPath, string? workingDir, TimeSpan timeout,
            CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = gitPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }
        if (workingDir is not null)
        {
            psi.WorkingDirectory = workingDir;
        }
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (askPassPath is not null)
        {
            psi.Environment["GIT_ASKPASS"] = askPassPath;
        }

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("git binary not found");
        using var timeoutCts = CancellationTokenSource
            .CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); }
            catch (Exception) { /* noop */ }
            throw;
        }
        return (p.ExitCode, await stdout, await stderr);
    }
}