using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Contracts;
using OpenHandsNET.Application.Scm;

namespace OpenHandsNET.Infrastructure.Scm;

/// <summary>
/// Base REST dos providers SPEC-53 (GitLab/Bitbucket/Azure DevOps): resolve
/// o token por-referência (<see cref="ScmConnection.PemSecretRef"/>) via
/// <see cref="ISecretStore"/> e monta o <see cref="HttpClient"/> por kind.
/// Nunca loga tokens.
/// </summary>
public abstract class RestScmProvider(
    IHttpClientFactory httpFactory, ISecretStore secrets) : IScmProvider
{
    /// <inheritdoc />
    public abstract ScmKind Kind { get; }

    /// <summary>Base URL efetiva da API do provider.</summary>
    protected abstract string ApiBase(ScmConnection c);

    /// <summary>Aplica a autenticação do kind no request.</summary>
    protected abstract void Authenticate(HttpRequestMessage req, string token);

    /// <summary>Resolve o token (nunca serializado/logado).</summary>
    protected async Task<string> TokenAsync(
        ScmConnection connection, CancellationToken ct)
    {
        var reference = connection.PemSecretRef
            ?? throw new InvalidOperationException(
                "scm connection missing token secret ref");
        return await secrets.ResolveAsync(
            "org", connection.OrgId?.ToString() ?? "", reference, ct)
            ?? throw new InvalidOperationException(
                $"token secret '{reference}' not found");
    }

    /// <summary>Request autenticado genérico.</summary>
    protected async Task<HttpResponseMessage> SendAsync(
        ScmConnection connection, HttpMethod method, string path,
        object? body, CancellationToken ct)
    {
        var http = httpFactory.CreateClient($"scm-{Kind.ToWire()}");
        var token = await TokenAsync(connection, ct);
        using var req = new HttpRequestMessage(method,
            $"{ApiBase(connection)}{path}");
        Authenticate(req, token);
        req.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }
        return await http.SendAsync(req, ct);
    }

    /// <summary>GET + parse JsonElement (null em não-sucesso).</summary>
    protected async Task<JsonElement?> GetJsonAsync(
        ScmConnection connection, string path, CancellationToken ct)
    {
        using var resp = await SendAsync(
            connection, HttpMethod.Get, path, null, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        return JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(ct)).RootElement.Clone();
    }

    /// <summary>Escapa segmentos de path ("owner/repo" → encoded).</summary>
    protected static string Enc(string value) =>
        Uri.EscapeDataString(value);

    /// <inheritdoc />
    public abstract Task<ScmPullRequest> CreatePullRequestAsync(
        ScmConnection connection, string repository,
        ScmPrCreateRequest request, CancellationToken ct);

    /// <inheritdoc />
    public abstract Task<ScmPullRequest?> GetPullRequestAsync(
        ScmConnection connection, string repository, int number,
        CancellationToken ct);

    /// <inheritdoc />
    public abstract Task<long> CreateIssueCommentAsync(
        ScmConnection connection, string repository, int issueNumber,
        string body, CancellationToken ct);

    /// <inheritdoc />
    public abstract Task<long> ReplyToReviewCommentAsync(
        ScmConnection connection, string repository, int pullRequestNumber,
        long inReplyToCommentId, string body, CancellationToken ct);

    /// <inheritdoc />
    public abstract Task SetCommitStatusAsync(
        ScmConnection connection, string repository, string sha,
        string state, string context, string? targetUrl,
        string? description, CancellationToken ct);

    /// <inheritdoc />
    public abstract Task<ScmConnectionDiagnostic> TestConnectionAsync(
        ScmConnection connection, CancellationToken ct);
}

/// <summary>GitLab REST v4 (SPEC-53 RF-001; self-managed via <c>ApiUrl</c>).</summary>
public sealed class GitLabScmProvider(
    IHttpClientFactory httpFactory, ISecretStore secrets,
    ILogger<GitLabScmProvider> logger)
    : RestScmProvider(httpFactory, secrets)
{
    /// <inheritdoc />
    public override ScmKind Kind => ScmKind.GitLab;

    /// <inheritdoc />
    protected override string ApiBase(ScmConnection c) =>
        $"{(c.ApiUrl ?? "https://gitlab.com").TrimEnd('/')}/api/v4";

    /// <inheritdoc />
    protected override void Authenticate(HttpRequestMessage req, string token) =>
        req.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);

    /// <inheritdoc />
    public override async Task<ScmPullRequest> CreatePullRequestAsync(
        ScmConnection connection, string repository,
        ScmPrCreateRequest request, CancellationToken ct)
    {
        using var resp = await SendAsync(connection, HttpMethod.Post,
            $"/projects/{Enc(repository)}/merge_requests",
            new
            {
                source_branch = request.Head,
                target_branch = request.Base ?? "main",
                title = request.Title,
                description = request.Body,
            }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"gitlab mr failed {(int)resp.StatusCode}: " +
                body[..Math.Min(300, body.Length)]);
        }
        var mr = JsonDocument.Parse(body).RootElement;
        return new ScmPullRequest(
            mr.GetProperty("web_url").GetString()!,
            mr.GetProperty("iid").GetInt32(),
            mr.GetProperty("state").GetString() ?? "opened",
            mr.TryGetProperty("sha", out var sha)
                ? sha.GetString() ?? "" : "",
            request.Title);
    }

    /// <inheritdoc />
    public override async Task<ScmPullRequest?> GetPullRequestAsync(
        ScmConnection connection, string repository, int number,
        CancellationToken ct)
    {
        var mr = await GetJsonAsync(connection,
            $"/projects/{Enc(repository)}/merge_requests/{number}", ct);
        if (mr is null)
        {
            return null;
        }
        var m = mr.Value;
        return new ScmPullRequest(
            m.GetProperty("web_url").GetString()!,
            m.GetProperty("iid").GetInt32(),
            m.GetProperty("state").GetString() ?? "",
            m.TryGetProperty("sha", out var sha)
                ? sha.GetString() ?? "" : "",
            m.TryGetProperty("title", out var t)
                ? t.GetString() ?? "" : "");
    }

    /// <inheritdoc />
    public override async Task<long> CreateIssueCommentAsync(
        ScmConnection connection, string repository, int issueNumber,
        string body, CancellationToken ct)
    {
        using var resp = await SendAsync(connection, HttpMethod.Post,
            $"/projects/{Enc(repository)}/issues/{issueNumber}/notes",
            new { body }, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"gitlab note failed {(int)resp.StatusCode}");
        }
        return JsonDocument.Parse(text).RootElement
            .GetProperty("id").GetInt64();
    }

    /// <inheritdoc />
    public override async Task<long> ReplyToReviewCommentAsync(
        ScmConnection connection, string repository, int pullRequestNumber,
        long inReplyToCommentId, string body, CancellationToken ct)
    {
        // GitLab: reply numa discussion de MR.
        using var resp = await SendAsync(connection, HttpMethod.Post,
            $"/projects/{Enc(repository)}/merge_requests/" +
            $"{pullRequestNumber}/discussions",
            new { body }, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"gitlab discussion failed {(int)resp.StatusCode}");
        }
        return JsonDocument.Parse(text).RootElement
            .GetProperty("id").GetString()?.GetHashCode() ?? 0;
    }

    /// <inheritdoc />
    public override async Task SetCommitStatusAsync(
        ScmConnection connection, string repository, string sha,
        string state, string context, string? targetUrl,
        string? description, CancellationToken ct)
    {
        using var resp = await SendAsync(connection, HttpMethod.Post,
            $"/projects/{Enc(repository)}/statuses/{sha}",
            new
            {
                state = state == "failure" ? "failed" : state,
                name = context,
                target_url = targetUrl,
                description,
            }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("gitlab status failed {Code}",
                (int)resp.StatusCode);
        }
    }

    /// <inheritdoc />
    public override async Task<ScmConnectionDiagnostic> TestConnectionAsync(
        ScmConnection connection, CancellationToken ct)
    {
        var user = await GetJsonAsync(connection, "/user", ct);
        var version = await GetJsonAsync(connection, "/version", ct);
        var projects = await GetJsonAsync(
            connection, "/projects?membership=true&per_page=1", ct);
        if (user is null)
        {
            return new ScmConnectionDiagnostic(
                false, null, null, null, "GET /user failed (token?)");
        }
        return new ScmConnectionDiagnostic(
            true,
            user.Value.TryGetProperty("username", out var u)
                ? u.GetString() : null,
            version?.TryGetProperty("version", out var v) == true
                ? v.GetString() : null,
            projects?.GetArrayLength() ?? 0,
            "ok");
    }
}

/// <summary>Bitbucket Cloud/DC (SPEC-53 RF-002).</summary>
public sealed class BitbucketScmProvider(
    IHttpClientFactory httpFactory, ISecretStore secrets,
    ILogger<BitbucketScmProvider> logger)
    : RestScmProvider(httpFactory, secrets)
{
    /// <inheritdoc />
    public override ScmKind Kind => ScmKind.Bitbucket;

    /// <inheritdoc />
    protected override string ApiBase(ScmConnection c) =>
        (c.ApiUrl ?? "https://api.bitbucket.org/2.0").TrimEnd('/');

    /// <inheritdoc />
    protected override void Authenticate(HttpRequestMessage req, string token) =>
        // app-password "user:pass" já vem base64-encodable via secret
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes(token)));

    /// <inheritdoc />
    public override async Task<ScmPullRequest> CreatePullRequestAsync(
        ScmConnection connection, string repository,
        ScmPrCreateRequest request, CancellationToken ct)
    {
        using var resp = await SendAsync(connection, HttpMethod.Post,
            $"/repositories/{repository}/pullrequests",
            new
            {
                title = request.Title,
                description = request.Body,
                source = new { branch = new { name = request.Head } },
                destination = new
                {
                    branch = new { name = request.Base ?? "main" },
                },
            }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"bitbucket pr failed {(int)resp.StatusCode}: " +
                body[..Math.Min(300, body.Length)]);
        }
        var pr = JsonDocument.Parse(body).RootElement;
        return new ScmPullRequest(
            pr.GetProperty("links").GetProperty("html")
                .GetProperty("href").GetString()!,
            pr.GetProperty("id").GetInt32(),
            pr.GetProperty("state").GetString() ?? "OPEN",
            pr.TryGetProperty("source", out var src)
                && src.TryGetProperty("commit", out var com)
                && com.TryGetProperty("hash", out var h)
                    ? h.GetString() ?? "" : "",
            request.Title);
    }

    /// <inheritdoc />
    public override async Task<ScmPullRequest?> GetPullRequestAsync(
        ScmConnection connection, string repository, int number,
        CancellationToken ct)
    {
        var pr = await GetJsonAsync(connection,
            $"/repositories/{repository}/pullrequests/{number}", ct);
        if (pr is null)
        {
            return null;
        }
        var p = pr.Value;
        return new ScmPullRequest(
            p.GetProperty("links").GetProperty("html")
                .GetProperty("href").GetString()!,
            p.GetProperty("id").GetInt32(),
            p.GetProperty("state").GetString() ?? "",
            "", p.TryGetProperty("title", out var t)
                ? t.GetString() ?? "" : "");
    }

    /// <inheritdoc />
    public override async Task<long> CreateIssueCommentAsync(
        ScmConnection connection, string repository, int issueNumber,
        string body, CancellationToken ct)
    {
        using var resp = await SendAsync(connection, HttpMethod.Post,
            $"/repositories/{repository}/issues/{issueNumber}/comments",
            new { content = new { raw = body } }, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"bitbucket comment failed {(int)resp.StatusCode}");
        }
        return JsonDocument.Parse(text).RootElement
            .GetProperty("id").GetInt64();
    }

    /// <inheritdoc />
    public override async Task<long> ReplyToReviewCommentAsync(
        ScmConnection connection, string repository, int pullRequestNumber,
        long inReplyToCommentId, string body, CancellationToken ct)
    {
        using var resp = await SendAsync(connection, HttpMethod.Post,
            $"/repositories/{repository}/pullrequests/" +
            $"{pullRequestNumber}/comments",
            new
            {
                content = new { raw = body },
                parent = new { id = inReplyToCommentId },
            }, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"bitbucket reply failed {(int)resp.StatusCode}");
        }
        return JsonDocument.Parse(text).RootElement
            .GetProperty("id").GetInt64();
    }

    /// <inheritdoc />
    public override async Task SetCommitStatusAsync(
        ScmConnection connection, string repository, string sha,
        string state, string context, string? targetUrl,
        string? description, CancellationToken ct)
    {
        using var resp = await SendAsync(connection, HttpMethod.Post,
            $"/repositories/{repository}/commit/{sha}/statuses/build",
            new
            {
                state = state == "failure" ? "FAILED"
                    : state == "success" ? "SUCCESSFUL"
                    : "INPROGRESS",
                key = context,
                url = targetUrl,
                description,
            }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("bitbucket status failed {Code}",
                (int)resp.StatusCode);
        }
    }

    /// <inheritdoc />
    public override async Task<ScmConnectionDiagnostic> TestConnectionAsync(
        ScmConnection connection, CancellationToken ct)
    {
        var user = await GetJsonAsync(connection, "/user", ct);
        if (user is null)
        {
            return new ScmConnectionDiagnostic(
                false, null, null, null, "GET /user failed (token?)");
        }
        return new ScmConnectionDiagnostic(
            true,
            user.Value.TryGetProperty("display_name", out var u)
                ? u.GetString() : null,
            null, null, "ok");
    }
}

/// <summary>Azure DevOps REST 7.1 (SPEC-53 RF-003).</summary>
public sealed class AzureDevOpsScmProvider(
    IHttpClientFactory httpFactory, ISecretStore secrets,
    ILogger<AzureDevOpsScmProvider> logger)
    : RestScmProvider(httpFactory, secrets)
{
    private const string ApiVersion = "api-version=7.1";

    /// <inheritdoc />
    public override ScmKind Kind => ScmKind.AzureDevOps;

    /// <inheritdoc />
    protected override string ApiBase(ScmConnection c) =>
        (c.ApiUrl ?? "https://dev.azure.com").TrimEnd('/') + "/_apis";

    /// <inheritdoc />
    protected override void Authenticate(HttpRequestMessage req, string token) =>
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes($":{token}")));

    /// <inheritdoc />
    public override async Task<ScmPullRequest> CreatePullRequestAsync(
        ScmConnection connection, string repository,
        ScmPrCreateRequest request, CancellationToken ct)
    {
        using var resp = await SendAsync(connection, HttpMethod.Post,
            $"/git/repositories/{Enc(repository)}/pullrequests?{ApiVersion}",
            new
            {
                sourceRefName = $"refs/heads/{request.Head}",
                targetRefName =
                    $"refs/heads/{request.Base ?? "main"}",
                title = request.Title,
                description = request.Body,
                isDraft = request.Draft,
            }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"azdo pr failed {(int)resp.StatusCode}: " +
                body[..Math.Min(300, body.Length)]);
        }
        var pr = JsonDocument.Parse(body).RootElement;
        return new ScmPullRequest(
            pr.GetProperty("_links").GetProperty("web")
                .GetProperty("href").GetString()!,
            pr.GetProperty("pullRequestId").GetInt32(),
            pr.GetProperty("status").GetString() ?? "active",
            pr.TryGetProperty("lastMergeSourceCommit", out var lc)
                && lc.TryGetProperty("commitId", out var cid)
                    ? cid.GetString() ?? "" : "",
            request.Title);
    }

    /// <inheritdoc />
    public override async Task<ScmPullRequest?> GetPullRequestAsync(
        ScmConnection connection, string repository, int number,
        CancellationToken ct)
    {
        var pr = await GetJsonAsync(connection,
            $"/git/repositories/{Enc(repository)}/pullrequests/{number}" +
            $"?{ApiVersion}", ct);
        if (pr is null)
        {
            return null;
        }
        var p = pr.Value;
        return new ScmPullRequest(
            p.GetProperty("_links").GetProperty("web")
                .GetProperty("href").GetString()!,
            p.GetProperty("pullRequestId").GetInt32(),
            p.GetProperty("status").GetString() ?? "",
            "", p.TryGetProperty("title", out var t)
                ? t.GetString() ?? "" : "");
    }

    /// <inheritdoc />
    public override async Task<long> CreateIssueCommentAsync(
        ScmConnection connection, string repository, int issueNumber,
        string body, CancellationToken ct)
    {
        // AzDO não tem "issue comment" genérico — work-item comment.
        using var resp = await SendAsync(connection, HttpMethod.Patch,
            $"/wit/workitems/{issueNumber}?{ApiVersion}",
            new object[]
            {
                new
                {
                    op = "add",
                    path = "/fields/System.History",
                    value = body,
                },
            }, ct);
        return resp.IsSuccessStatusCode ? issueNumber : 0;
    }

    /// <inheritdoc />
    public override async Task<long> ReplyToReviewCommentAsync(
        ScmConnection connection, string repository, int pullRequestNumber,
        long inReplyToCommentId, string body, CancellationToken ct)
    {
        using var resp = await SendAsync(connection, HttpMethod.Post,
            $"/git/repositories/{Enc(repository)}/pullRequests/" +
            $"{pullRequestNumber}/threads?{ApiVersion}",
            new
            {
                comments = new[]
                {
                    new
                    {
                        parentCommentId = inReplyToCommentId,
                        content = body,
                        commentType = 1,
                    },
                },
                status = 1,
            }, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"azdo thread failed {(int)resp.StatusCode}");
        }
        return JsonDocument.Parse(text).RootElement
            .GetProperty("id").GetInt64();
    }

    /// <inheritdoc />
    public override async Task SetCommitStatusAsync(
        ScmConnection connection, string repository, string sha,
        string state, string context, string? targetUrl,
        string? description, CancellationToken ct)
    {
        using var resp = await SendAsync(connection, HttpMethod.Post,
            $"/git/repositories/{Enc(repository)}/commits/{sha}/statuses" +
            $"?{ApiVersion}",
            new
            {
                state = state == "failure" ? "failed"
                    : state == "success" ? "succeeded" : "pending",
                context = new { name = context, genre = "openhandsnet" },
                targetUrl,
                description,
            }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("azdo status failed {Code}",
                (int)resp.StatusCode);
        }
    }

    /// <inheritdoc />
    public override async Task<ScmConnectionDiagnostic> TestConnectionAsync(
        ScmConnection connection, CancellationToken ct)
    {
        var repos = await GetJsonAsync(
            connection, $"/git/repositories?{ApiVersion}", ct);
        if (repos is null)
        {
            return new ScmConnectionDiagnostic(
                false, null, null, null,
                "GET /_apis/git/repositories failed (token/org?)");
        }
        var count = repos.Value.TryGetProperty("count", out var cn)
            ? cn.GetInt32() : 0;
        return new ScmConnectionDiagnostic(true, null, null, count, "ok");
    }
}