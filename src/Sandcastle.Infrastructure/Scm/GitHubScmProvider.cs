using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

using Octokit;

using Sandcastle.Application.Contracts;
using Sandcastle.Application.Scm;

namespace Sandcastle.Infrastructure.Scm;

/// <summary>
/// Builds an authenticated <see cref="IGitHubClient"/> for a connection —
/// App-JWT → installation access token (cached ~50 min, never persisted).
/// Seams tests via a fake factory.
/// </summary>
public interface IGitHubClientFactory
{
    /// <summary>Client autenticado como instalação da conexão.</summary>
    Task<IGitHubClient> ForConnectionAsync(
        ScmConnection connection, CancellationToken ct);

    /// <summary>
    /// SPEC-141 RF-003: installation access token cru (mesmo cache de
    /// ~50 min) — usado p/ provisionar git over https no sandbox do fix;
    /// nunca logado/persistido.
    /// </summary>
    Task<string> GetInstallationTokenAsync(
        ScmConnection connection, CancellationToken ct);
}

/// <summary>
/// <see cref="IScmProvider"/> GitHub via Octokit (SPEC-52 RF-001/003/005).
/// Tokens e PEM nunca são logados.
/// </summary>
public sealed class GitHubScmProvider(
    IGitHubClientFactory clients,
    ILogger<GitHubScmProvider> logger) : IScmProvider
{
    /// <inheritdoc />
    public ScmKind Kind => ScmKind.GitHub;

    private static (string Owner, string Name) SplitRepo(string repository)
    {
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new ArgumentException(
                $"repository must be 'owner/repo', got '{repository}'",
                nameof(repository));
        return (parts[0], parts[1]);
    }

    /// <inheritdoc />
    public async Task<ScmPullRequest> CreatePullRequestAsync(
        ScmConnection connection, string repository,
        ScmPrCreateRequest request, CancellationToken ct)
    {
        var (owner, name) = SplitRepo(repository);
        var client = await clients.ForConnectionAsync(connection, ct);
        var create = new NewPullRequest(request.Title, request.Head,
            request.Base ?? "main")
        {
            Body = request.Body,
            Draft = request.Draft,
        };
        var pr = await client.PullRequest.Create(owner, name, create);
        if (request.Reviewers is { Count: > 0 } reviewers)
        {
            try
            {
                await client.PullRequest.ReviewRequest.Create(owner, name,
                    pr.Number, new PullRequestReviewRequest(reviewers, []));
            }
            catch (Exception e)
            {
                logger.LogWarning(e,
                    "reviewer request failed for pr {Number} ({Owner}/{Name})",
                    pr.Number, owner, name);
            }
        }
        return new ScmPullRequest(pr.HtmlUrl, pr.Number,
            pr.State.StringValue, pr.Head.Sha, pr.Title);
    }

    /// <inheritdoc />
    public async Task<ScmPullRequest?> GetPullRequestAsync(
        ScmConnection connection, string repository, int number,
        CancellationToken ct)
    {
        var (owner, name) = SplitRepo(repository);
        var client = await clients.ForConnectionAsync(connection, ct);
        try
        {
            var pr = await client.PullRequest.Get(owner, name, number);
            // SPEC-143: reconciliação merged precisa diferenciar
            // closed ≠ merged (Octokit expõe Merged no GET).
            return new ScmPullRequest(pr.HtmlUrl, pr.Number,
                pr.State.StringValue, pr.Head.Sha, pr.Title, pr.Merged);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<long> CreateIssueCommentAsync(
        ScmConnection connection, string repository, int issueNumber,
        string body, CancellationToken ct)
    {
        var (owner, name) = SplitRepo(repository);
        var client = await clients.ForConnectionAsync(connection, ct);
        var comment = await client.Issue.Comment.Create(
            owner, name, issueNumber, body);
        return comment.Id;
    }

    /// <inheritdoc />
    public async Task<long> ReplyToReviewCommentAsync(
        ScmConnection connection, string repository, int pullRequestNumber,
        long inReplyToCommentId, string body, CancellationToken ct)
    {
        var (owner, name) = SplitRepo(repository);
        var client = await clients.ForConnectionAsync(connection, ct);
        var reply = await client.PullRequest.ReviewComment.CreateReply(
            owner, name, pullRequestNumber,
            new PullRequestReviewCommentReplyCreate(
                body, (int)inReplyToCommentId));
        return reply.Id;
    }

    /// <inheritdoc />
    public async Task SetCommitStatusAsync(
        ScmConnection connection, string repository, string sha,
        string state, string context, string? targetUrl,
        string? description, CancellationToken ct)
    {
        var (owner, name) = SplitRepo(repository);
        var client = await clients.ForConnectionAsync(connection, ct);
        var status = new NewCommitStatus
        {
            State = state switch
            {
                "success" => CommitState.Success,
                "failure" => CommitState.Failure,
                "error" => CommitState.Error,
                _ => CommitState.Pending,
            },
            Context = context,
            TargetUrl = targetUrl,
            Description = description,
        };
        await client.Repository.Status.Create(owner, name, sha, status);
    }

    /// <inheritdoc />
    public async Task<ScmConnectionDiagnostic> TestConnectionAsync(
        ScmConnection connection, CancellationToken ct)
    {
        try
        {
            var client = await clients.ForConnectionAsync(connection, ct);
            var user = await client.User.Current();
            return new ScmConnectionDiagnostic(
                true, user.Login, null, null, "ok");
        }
        catch (Exception e)
        {
            return new ScmConnectionDiagnostic(
                false, null, null, null, e.Message);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScmPullRequestFile>> GetPullRequestFilesAsync(
        ScmConnection connection, string repository, int pullRequestNumber,
        CancellationToken ct)
    {
        var (owner, name) = SplitRepo(repository);
        var client = await clients.ForConnectionAsync(connection, ct);
        var files = await client.PullRequest.Files(
            owner, name, pullRequestNumber);
        return files.Select(f => new ScmPullRequestFile(
            f.FileName, f.PreviousFileName, f.Status,
            f.Additions, f.Deletions, f.Patch)).ToList();
    }

    /// <inheritdoc />
    public async Task<string?> GetFileContentAsync(
        ScmConnection connection, string repository, string path,
        string? reference, CancellationToken ct)
    {
        var (owner, name) = SplitRepo(repository);
        var client = await clients.ForConnectionAsync(connection, ct);
        try
        {
            var raw = await client.Repository.Content.GetRawContentByRef(
                owner, name, path, reference);
            return Encoding.UTF8.GetString(raw);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListDirectoryAsync(
        ScmConnection connection, string repository, string directoryPath,
        string? reference, CancellationToken ct)
    {
        var (owner, name) = SplitRepo(repository);
        var client = await clients.ForConnectionAsync(connection, ct);
        try
        {
            var entries = await client.Repository.Content
                .GetAllContentsByRef(owner, name, directoryPath, reference);
            return entries
                .Where(e => e.Type == ContentType.File)
                .Select(e => e.Path).ToList();
        }
        catch (NotFoundException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<ScmReviewResult?> CreateReviewAsync(
        ScmConnection connection, string repository, int pullRequestNumber,
        string headSha, string body,
        IReadOnlyList<ScmReviewComment> comments, CancellationToken ct)
    {
        var (owner, name) = SplitRepo(repository);
        var client = await clients.ForConnectionAsync(connection, ct);

        // Octokit 14 só expõe DraftPullRequestReviewComment por *position*
        // (deprecado) — chamamos o endpoint direto com line+side ("RIGHT"),
        // que é o ancoramento atual do GitHub.
        var uri = ApiUrls.PullRequestReviews(owner, name, pullRequestNumber);
        object Payload(IReadOnlyList<ScmReviewComment> cs) => new
        {
            commit_id = headSha,
            body,
            @event = "COMMENT",
            comments = cs.Select(c => new
            {
                path = c.Path,
                line = c.Line,
                side = "RIGHT",
                body = c.Body,
            }).ToArray(),
        };

        try
        {
            var review = await PostReviewAsync(
                client, uri, Payload(comments), ct);
            return new ScmReviewResult(
                review.ReviewId, review.Url, comments.Count);
        }
        catch (ApiException e) when (comments.Count > 0)
        {
            // Âncora inválida (linha fora do diff, path renomeado…) — retenta
            // sem inline comments: o summary já traz os findings agrupados.
            logger.LogWarning(e,
                "github review com {Count} comments falhou ({Code}) — "
                + "retentando só com body (pr {Pr} {Repo})",
                comments.Count, (int)e.StatusCode, pullRequestNumber,
                repository);
            var review = await PostReviewAsync(
                client, uri, Payload([]), ct);
            return new ScmReviewResult(review.ReviewId, review.Url, 0);
        }
    }

    private static async Task<(long? ReviewId, string? Url)> PostReviewAsync(
        IGitHubClient client, Uri uri, object payload, CancellationToken ct)
    {
        var resp = await client.Connection.Post<PullRequestReview>(
            uri, payload, "application/vnd.github+json",
            "application/json", (string?)null, ct);
        return (resp.Body.Id, resp.Body.HtmlUrl);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScmCheckRun>> GetCheckRunsAsync(
        ScmConnection connection, string repository, string reference,
        CancellationToken ct)
    {
        var (owner, name) = SplitRepo(repository);
        var client = await clients.ForConnectionAsync(connection, ct);
        var resp = await client.Check.Run.GetAllForReference(
            owner, name, reference);
        return resp.CheckRuns.Select(c => new ScmCheckRun(
            c.Id, c.Name, c.Status.StringValue,
            c.Conclusion?.StringValue, c.HeadSha, c.DetailsUrl)).ToList();
    }

    /// <inheritdoc />
    public async Task<string?> GetCheckRunLogsAsync(
        ScmConnection connection, string repository, long checkRunId,
        CancellationToken ct)
    {
        var (owner, name) = SplitRepo(repository);
        var client = await clients.ForConnectionAsync(connection, ct);
        var run = await client.Check.Run.Get(owner, name, checkRunId);
        var parts = new[] {
            run.Output?.Title, run.Output?.Summary, run.Output?.Text }
            .Where(s => !string.IsNullOrEmpty(s));
        var joined = string.Join("\n", parts!);
        return joined.Length == 0 ? null : joined;
    }

    /// <inheritdoc />
    public async Task<string?> GetWorkflowRunLogsAsync(
        ScmConnection connection, string repository, long workflowRunId,
        CancellationToken ct)
    {
        var (owner, name) = SplitRepo(repository);
        var client = await clients.ForConnectionAsync(connection, ct);
        byte[] zip;
        try
        {
            zip = await client.Actions.Workflows.Runs.GetLogs(
                owner, name, workflowRunId);
        }
        catch (NotFoundException)
        {
            return null;
        }
        if (zip is null || zip.Length == 0)
        {
            return null;
        }
        return ExtractZipText(zip);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScmCommitPullRequest>>
        GetPullRequestsForCommitAsync(
            ScmConnection connection, string repository, string sha,
            CancellationToken ct)
    {
        var (owner, name) = SplitRepo(repository);
        var client = await clients.ForConnectionAsync(connection, ct);
        // Octokit não expõe commits/{sha}/pulls — chamada direta.
        var resp = await client.Connection.Get<List<PullRequest>>(
            new Uri($"repos/{owner}/{name}/commits/{sha}/pulls",
                UriKind.Relative),
            null, "application/vnd.github+json");
        return resp.Body.Select(p => new ScmCommitPullRequest(
            p.Number, p.State.StringValue, p.Head.Sha, p.Head.Ref,
            p.Head.Repository?.FullName ?? repository)).ToList();
    }

    /// <summary>Extrai e concatena os .txt do zip de logs do Actions.</summary>
    private static string? ExtractZipText(byte[] zip)
    {
        try
        {
            using var archive = new System.IO.Compression.ZipArchive(
                new MemoryStream(zip), System.IO.Compression.ZipArchiveMode.Read);
            var sb = new StringBuilder();
            foreach (var entry in archive.Entries
                .Where(e => e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                sb.Append("\n==== ").Append(entry.FullName).Append(" ====\n");
                using var reader = new StreamReader(entry.Open());
                sb.Append(reader.ReadToEnd());
            }
            return sb.Length == 0 ? null : sb.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Default factory: resolves the connection's PEM via <c>ISecretStore</c>,
/// mints a GitHub App JWT, exchanges for an installation token cached ~50 min
/// in <see cref="IDistributedCache"/> (SPEC-52 RF-002 — token nunca persistido).
/// </summary>
public sealed class GitHubAppClientFactory(
    ISecretStore secrets,
    IDistributedCache cache,
    ILogger<GitHubAppClientFactory> logger) : IGitHubClientFactory
{
    private static readonly ProductHeaderValue ProductHeader =
        new("Sandcastle");

    /// <inheritdoc />
    public async Task<IGitHubClient> ForConnectionAsync(
        ScmConnection connection, CancellationToken ct) =>
        new GitHubClient(ProductHeader)
        {
            Credentials = new Credentials(
                await GetInstallationTokenAsync(connection, ct)),
        };

    /// <inheritdoc />
    public async Task<string> GetInstallationTokenAsync(
        ScmConnection connection, CancellationToken ct)
    {
        var cacheKey = $"scm:gh:install-token:{connection.InstallationId}";
        var cached = await cache.GetStringAsync(cacheKey, ct);
        if (!string.IsNullOrEmpty(cached))
        {
            return cached;
        }

        if (connection.AppId is null || connection.PemSecretRef is null
            || connection.InstallationId is null)
        {
            throw new InvalidOperationException(
                "scm connection missing app_id/pem/installation_id");
        }

        var pem = await secrets.ResolveAsync(
            "org", connection.OrgId?.ToString() ?? "",
            connection.PemSecretRef, ct)
            ?? throw new InvalidOperationException(
                $"pem secret '{connection.PemSecretRef}' not found");

        var appClient = new GitHubClient(ProductHeader)
        {
            Credentials = new Credentials(
                CreateAppJwt(pem, connection.AppId)),
        };

        var token = await appClient.GitHubApps.CreateInstallationToken(
            long.Parse(connection.InstallationId));
        await cache.SetStringAsync(cacheKey, token.Token,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(50),
            }, ct);
        logger.LogDebug("github installation token minted (conn {Id})",
            connection.Id);
        return token.Token;
    }

    /// <summary>
    /// JWT RS256 do GitHub App (iss=app_id, exp ≤ 10min) — Octokit 14 removeu
    /// os helpers <c>GitHubJwtFactory</c>, então assinamos manualmente.
    /// A PEM nunca é logada/persistida.
    /// </summary>
    private static string CreateAppJwt(string pem, string appId)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(
            new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = appId,
        }));
        var input = string.Create(
            CultureInfo.InvariantCulture, $"{header}.{payload}");
        // RS256 (RFC 7518) exige padding PKCS1 — exigência do GitHub App JWT.
        var signature = Base64Url(rsa.SignData( // NOSONAR S5542
            Encoding.ASCII.GetBytes(input),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return string.Create(
            CultureInfo.InvariantCulture, $"{input}.{signature}");
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');
}

/// <summary>Keyed registry (SPEC-52 RF-001; SPEC-53 adiciona os demais kinds).</summary>
public sealed class ScmProviderRegistry(
    IEnumerable<IScmProvider> providers) : IScmProviderRegistry
{
    /// <inheritdoc />
    public IScmProvider? Get(ScmKind kind) =>
        providers.FirstOrDefault(p => p.Kind == kind);
}