using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Octokit;
using OpenHandsNET.Application.Contracts;
using OpenHandsNET.Application.Scm;

namespace OpenHandsNET.Infrastructure.Scm;

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
            return new ScmPullRequest(pr.HtmlUrl, pr.Number,
                pr.State.StringValue, pr.Head.Sha, pr.Title);
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
        new("OpenHandsNET");

    /// <inheritdoc />
    public async Task<IGitHubClient> ForConnectionAsync(
        ScmConnection connection, CancellationToken ct)
    {
        var cacheKey = $"scm:gh:install-token:{connection.InstallationId}";
        var cached = await cache.GetStringAsync(cacheKey, ct);
        if (!string.IsNullOrEmpty(cached))
        {
            return new GitHubClient(ProductHeader)
            {
                Credentials = new Credentials(cached),
            };
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
        return new GitHubClient(ProductHeader)
        {
            Credentials = new Credentials(token.Token),
        };
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
        var signature = Base64Url(rsa.SignData(
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
