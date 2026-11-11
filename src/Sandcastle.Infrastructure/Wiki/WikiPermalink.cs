using Sandcastle.Application.Scm;

namespace Sandcastle.Infrastructure.Wiki;

/// <summary>
/// Provider permalink <c>{repo}/blob/{sha}/{path}#Lx-Ly</c>
/// (SPEC-145 RF-001/RF-002): a mesma forma usada pelas citações do Ask
/// e pelos links reescritos das páginas da wiki. Null quando não há
/// commit sha — citações sem âncora permanecem texto puro.
/// </summary>
public static class WikiPermalink
{
    /// <summary>Base URL do repo no provider (ApiUrl cobre self-hosted).</summary>
    public static string RepoBaseUrl(
        ScmConnection? connection, string repository)
    {
        var baseUrl = connection?.ApiUrl?.TrimEnd('/');
        var kind = connection?.Kind ?? ScmKind.GitHub;
        return baseUrl is not null
            ? $"{baseUrl}/{repository}"
            : kind switch
            {
                ScmKind.GitLab => $"https://gitlab.com/{repository}",
                ScmKind.Bitbucket => $"https://bitbucket.org/{repository}",
                ScmKind.AzureDevOps =>
                    $"https://dev.azure.com/{repository}",
                _ => $"https://github.com/{repository}",
            };
    }

    /// <summary>
    /// Link do arquivo no commit (github/gitlab/bitbucket) — null sem sha.
    /// </summary>
    public static string? FileLink(
        ScmConnection? connection, string repository,
        string? commitSha, string path)
    {
        if (string.IsNullOrEmpty(commitSha))
        {
            return null;
        }

        var safePath = path.Replace('\\', '/').TrimStart('/');
        var repoBase = RepoBaseUrl(connection, repository);
        return (connection?.Kind ?? ScmKind.GitHub) switch
        {
            ScmKind.GitLab => $"{repoBase}/-/blob/{commitSha}/{safePath}",
            ScmKind.Bitbucket => $"{repoBase}/src/{commitSha}/{safePath}",
            _ => $"{repoBase}/blob/{commitSha}/{safePath}",
        };
    }

    /// <summary>Link ancorado em linhas — <c>#Lx-Ly</c> (single line: #Lx).</summary>
    public static string? Build(
        ScmConnection? connection, string repository,
        string? commitSha, string path, int startLine, int? endLine)
    {
        var file = FileLink(connection, repository, commitSha, path);
        if (file is null)
        {
            return null;
        }

        var end = endLine is { } e && e > startLine ? e : (int?)null;
        var anchor = (connection?.Kind ?? ScmKind.GitHub) switch
        {
            ScmKind.Bitbucket => end is { } ee
                ? $"#lines-{startLine}:{ee}" : $"#lines-{startLine}",
            _ => end is { } ee2
                ? $"#L{startLine}-L{ee2}" : $"#L{startLine}",
        };
        return file + anchor;
    }
}