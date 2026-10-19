using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using OpenHandsNET.Application.Contracts;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Application.Scm;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Infrastructure.Scm;

using Shouldly;

using Xunit;

namespace OpenHandsNET.UnitTests.Scm;

/// <summary>
/// SPEC-53 — webhooks por kind (GitLab token, Bitbucket HMAC, AzDO shared
/// secret), roteamento de menção → start-task, e diagnóstico
/// <c>POST /scm/connections/{id}/test</c>.
/// </summary>
public sealed class Spec53ScmProvidersTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly Infrastructure.Persistence.AppDbContext _db;
    private readonly Guid _orgId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly FakeConversations _conversations = new();
    private readonly FakeAudit _audit = new();
    private readonly FakeSecrets _secrets = new();
    private readonly GitHubWebhookService _webhook;
    private readonly ScmConnectionService _connections;

    public Spec53ScmProvidersTests()
    {
        _conn.Open();
        _db = new Infrastructure.Persistence.AppDbContext(
            new DbContextOptionsBuilder<Infrastructure.Persistence
                .AppDbContext>()
                .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
        _webhook = new GitHubWebhookService(_db, _secrets, _conversations,
            _audit, NullLogger<GitHubWebhookService>.Instance);
        _connections = new ScmConnectionService(_db, _secrets, _audit,
            new HttpFactory(), new FakeRegistry(),
            NullLogger<ScmConnectionService>.Instance);
        _db.OrgMembers.Add(new OrgMemberRecord
        {
            OrgId = _orgId,
            UserId = _ownerId,
            Role = "owner",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
    }

    private void Seed(string kind, string repo) =>
        _db.ScmConnections.Add(new ScmConnectionRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            Kind = kind,
            WebhookSecretRef = "hook_secret",
            ReposJson = $"[\"{repo}\"]",
            CreatedByUserId = _ownerId.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        });

    private static byte[] P(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public async Task GitLab_Webhook_BadToken_ReturnsInvalidSignature()
    {
        Seed("gitlab", "grp/repo");
        await _db.SaveChangesAsync();
        var payload = P("""{"project":{"path_with_namespace":"grp/repo"}}""");
        var result = await _webhook.HandleAsync(ScmKind.GitLab,
            "wrong", "Note Hook", "uuid-1", payload,
            CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.InvalidSignature);
    }

    [Fact]
    public async Task GitLab_NoteHook_Mention_StartsConversation()
    {
        Seed("gitlab", "grp/repo");
        await _db.SaveChangesAsync();
        var payload = P("""
        {
          "object_kind": "note",
          "project": {"path_with_namespace": "grp/repo"},
          "user": {"username": "afonso"},
          "merge_request": {"iid": 3, "source_branch": "feat-x",
                            "url": "https://gl/grp/repo/-/merge_requests/3"},
          "object_attributes": {"noteable_type": "MergeRequest",
                                "note": "@openhandsnet fix the tests"}
        }
        """);
        var result = await _webhook.HandleAsync(ScmKind.GitLab,
            "hooksecret", "Note Hook", "uuid-2", payload,
            CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.Processed);
        _conversations.Calls.ShouldHaveSingleItem();
        _conversations.Calls[0].Request.SelectedRepository
            .ShouldBe("grp/repo");
        _audit.Actions.ShouldContain("scm.webhook_triggered");
    }

    [Fact]
    public async Task Bitbucket_HmacSignature_Mention_Processed()
    {
        Seed("bitbucket", "ws/repo");
        await _db.SaveChangesAsync();
        var payload = P("""
        {
          "repository": {"full_name": "ws/repo"},
          "pullrequest": {"id": 5,
            "links": {"html": {"href": "https://bb/ws/repo/pull-requests/5"}}},
          "comment": {"content": {"raw": "@openhandsnet review this"},
                      "user": {"nickname": "afonso"}}
        }
        """);
        var sig = "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes("hooksecret"),
                payload));
        var result = await _webhook.HandleAsync(ScmKind.Bitbucket,
            sig, "pullrequest:comment_created", "req-1", payload,
            CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.Processed);
        _conversations.Calls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AzureDevOps_SharedSecret_Comment_Processed()
    {
        Seed("azuredevops", "proj-repo");
        await _db.SaveChangesAsync();
        var payload = P("""
        {
          "eventType": "ms.vss-code.git-pullrequest-comment-event",
          "notificationId": 9,
          "resource": {
            "repository": {"name": "proj-repo"},
            "pullRequest": {"pullRequestId": 12},
            "comment": {"content": "@openhandsnet apply the fix",
                        "author": {"displayName": "Afonso"}}
          }
        }
        """);
        var result = await _webhook.HandleAsync(ScmKind.AzureDevOps,
            "hooksecret", "ms.vss-code.git-pullrequest-comment-event",
            "9", payload, CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.Processed);
        _conversations.Calls.ShouldHaveSingleItem();
        _conversations.Calls[0].Request.SelectedRepository
            .ShouldBe("proj-repo");
    }

    [Fact]
    public async Task AzureDevOps_WrongSecret_Rejected()
    {
        Seed("azuredevops", "proj-repo");
        await _db.SaveChangesAsync();
        var payload = P("""
        {"eventType":"x","resource":{"repository":{"name":"proj-repo"},
         "comment":{"content":"@openhandsnet hi"}}}
        """);
        var result = await _webhook.HandleAsync(ScmKind.AzureDevOps,
            "nope", "x", "1", payload, CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.InvalidSignature);
    }

    [Fact]
    public async Task UnknownRepo_NoConnection_404Path()
    {
        var payload = P("""{"project":{"path_with_namespace":"nope/r"}}""");
        var result = await _webhook.HandleAsync(ScmKind.GitLab,
            "hooksecret", "Note Hook", "u", payload,
            CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.NoConnection);
    }

    [Fact]
    public async Task TestAsync_ReturnsDiagnostics()
    {
        var conn = await _connections.CreateAsync(
            _orgId, ScmKind.GitLab, null, "tok_ref", null, null,
            null, null, _ownerId.ToString(), CancellationToken.None,
            apiUrl: "https://gitlab.example.com");
        conn.ApiUrl.ShouldBe("https://gitlab.example.com");

        var diag = await _connections.TestAsync(conn.Id,
            CancellationToken.None);
        diag.Ok.ShouldBeTrue();
        diag.User.ShouldBe("fake-user");

        var missing = await _connections.TestAsync(Guid.NewGuid(),
            CancellationToken.None);
        missing.Ok.ShouldBeFalse();
    }

    [Fact]
    public void Registry_ResolvesAllKinds()
    {
        var registry = new ScmProviderRegistry([
            new StubProvider(ScmKind.GitHub),
            new StubProvider(ScmKind.GitLab),
            new StubProvider(ScmKind.Bitbucket),
            new StubProvider(ScmKind.AzureDevOps),
        ]);
        registry.Get(ScmKind.GitHub)!.Kind.ShouldBe(ScmKind.GitHub);
        registry.Get(ScmKind.GitLab)!.Kind.ShouldBe(ScmKind.GitLab);
        registry.Get(ScmKind.Bitbucket)!.Kind.ShouldBe(ScmKind.Bitbucket);
        registry.Get(ScmKind.AzureDevOps)!.Kind.ShouldBe(ScmKind.AzureDevOps);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private sealed class StubProvider(ScmKind kind) : IScmProvider
    {
        public ScmKind Kind => kind;
        public Task<ScmPullRequest> CreatePullRequestAsync(
            ScmConnection c, string r, ScmPrCreateRequest q,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<ScmPullRequest?> GetPullRequestAsync(
            ScmConnection c, string r, int n, CancellationToken ct) =>
            Task.FromResult<ScmPullRequest?>(null);
        public Task<long> CreateIssueCommentAsync(ScmConnection c, string r,
            int n, string b, CancellationToken ct) => Task.FromResult(0L);
        public Task<long> ReplyToReviewCommentAsync(ScmConnection c,
            string r, int p, long i, string b, CancellationToken ct) =>
            Task.FromResult(0L);
        public Task SetCommitStatusAsync(ScmConnection c, string r,
            string s, string st, string ctx, string? t, string? d,
            CancellationToken ct) => Task.CompletedTask;
        public Task<ScmConnectionDiagnostic> TestConnectionAsync(
            ScmConnection c, CancellationToken ct) =>
            Task.FromResult(new ScmConnectionDiagnostic(
                true, "fake-user", null, null, "ok"));
    }

    private sealed class FakeRegistry : IScmProviderRegistry
    {
        public IScmProvider? Get(ScmKind kind) =>
            new StubProvider(kind);
    }

    private sealed class HttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class FakeSecrets : ISecretStore
    {
        public Task<string?> ResolveAsync(string scope, string ownerId,
            string name, CancellationToken ct) =>
            Task.FromResult<string?>("hooksecret");
        public Task<IReadOnlyList<string>> ListNamesAsync(string scope,
            string ownerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task SetAsync(string scope, string ownerId, string name,
            string plaintext, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> DeleteAsync(string scope, string ownerId,
            string name, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class FakeAudit : IAuditService
    {
        public List<string> Actions { get; } = [];
        public Task RecordAsync(string action, string? actorUserId = null,
            Guid? orgId = null, string? targetType = null,
            string? targetId = null, string? ip = null,
            string? userAgent = null, object? metadata = null,
            CancellationToken cancellationToken = default)
        { Actions.Add(action); return Task.CompletedTask; }
        public Task<(IReadOnlyList<AuditEntry> Items, int Total)> QueryAsync(
            string? u, string? a, DateTimeOffset? f, DateTimeOffset? t,
            int p, int ps, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<AuditEntry>, int)>(([], 0));
    }

    private sealed class FakeConversations
        : OpenHandsNET.Application.Conversations.IAppConversationService
    {
        public List<(Guid UserId, Guid? OrgId, CreateConversationRequest
            Request)> Calls
        { get; } = [];

        public Task<AppConversationStartTask> CreateAsync(
            CreateConversationRequest request, CancellationToken ct) =>
            Task.FromResult(Task0());
        public Task<AppConversationStartTask> CreateForUserAsync(
            Guid userId, Guid? orgId, CreateConversationRequest request,
            CancellationToken ct)
        { Calls.Add((userId, orgId, request)); return Task.FromResult(Task0()); }
        private static AppConversationStartTask Task0() => new(
            Guid.NewGuid(), StartTaskStatus.Working, Guid.NewGuid(),
            null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        public Task<ConversationPage> SearchAsync(ConversationQuery q,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<long> CountAsync(ConversationQuery q,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<AppConversation>> GetBatchAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<AppConversation?> GetAsync(Guid id,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<AppConversation?> UpdateAsync(Guid id, string? t,
            IReadOnlyList<string>? tags, bool? p, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<SendMessageError?> ValidateSendMessageAsync(Guid id,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<SwitchResult> SwitchProfileAsync(Guid id,
            string? profile, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<SwitchResult> SwitchModelAsync(Guid id, string model,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<AppConversation?> SetPermissionsAsync(Guid id,
            IReadOnlyList<Application.Agent.AgentPermissionRule>? rules,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<AppConversation?> SetModeAsync(Guid id, string mode,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<AttentionItem>> ListAttentionAsync(
            int limit, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<AttentionDismissResult> DismissAttentionAsync(Guid id,
            CancellationToken ct) => throw new NotImplementedException();
    }
}