using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenHandsNET.Application.Contracts;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Application.Git;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Application.Scm;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Scm;
using Shouldly;
using Xunit;
using SandboxModel = OpenHandsNET.Domain.Sandbox.Sandbox;

namespace OpenHandsNET.UnitTests.Scm;

/// <summary>
/// SPEC-52 — webhook GitHub (HMAC + idempotência + @menção → start-task),
/// manifest flow e criação de PR a partir da conversa.
/// </summary>
public sealed class Spec52ScmTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly Infrastructure.Persistence.AppDbContext _db;
    private readonly Guid _orgId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly CapturingConversations _conversations = new();
    private readonly CapturingAudit _audit = new();
    private readonly FakeSecrets _secrets = new();
    private readonly GitHubWebhookService _webhook;
    private readonly ScmConnectionService _connections;

    public Spec52ScmTests()
    {
        _conn.Open();
        _db = new Infrastructure.Persistence.AppDbContext(
            new DbContextOptionsBuilder<Infrastructure.Persistence.AppDbContext>()
                .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
        _webhook = new GitHubWebhookService(_db, _secrets, _conversations,
            _audit, NullLogger<GitHubWebhookService>.Instance);
        _connections = new ScmConnectionService(_db, _secrets, _audit,
            new FakeHttpFactory(), new FakeRegistry(),
            NullLogger<ScmConnectionService>.Instance);
        _db.ScmConnections.Add(new ScmConnectionRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            Kind = "github",
            InstallationId = "777",
            PemSecretRef = "scm_github_pem_test",
            WebhookSecretRef = "scm_github_hook_test",
            CreatedByUserId = _ownerId.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
        _secrets.Value = "hooksecret";
    }

    private static string Sign(byte[] payload)
    {
        var hex = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("hooksecret"), payload));
        return $"sha256={hex}";
    }

    private static byte[] MentionPayload(string deliveryText = "fix lint") =>
        Encoding.UTF8.GetBytes(
            "{\"action\":\"created\",\"installation\":{\"id\":777},"
            + "\"repository\":{\"full_name\":\"afonsoft/demo\"},"
            + "\"issue\":{\"number\":4},"
            + $"\"comment\":{{\"id\":99,\"body\":\"@openhandsnet {deliveryText}\","
            + "\"user\":{\"login\":\"afonso\"}}}");

    [Fact]
    public async Task Webhook_bad_signature_is_rejected()
    {
        var result = await _webhook.HandleGitHubAsync(
            "sha256=deadbeef", "issue_comment", "del-1",
            MentionPayload(), CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.InvalidSignature);
        _conversations.Calls.ShouldBeEmpty();
        _audit.Actions.ShouldContain("scm.webhook_rejected");
    }

    [Fact]
    public async Task Webhook_mention_creates_start_task()
    {
        var payload = MentionPayload();
        var result = await _webhook.HandleGitHubAsync(
            Sign(payload), "issue_comment", "del-2", payload,
            CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.Processed);
        var call = _conversations.Calls.ShouldHaveSingleItem();
        call.UserId.ShouldBe(_ownerId);
        call.Request.SelectedRepository.ShouldBe("afonsoft/demo");
        var prompt = call.Request.InitialMessage!.Content[0].ToString()!;
        prompt.ShouldContain("fix lint");
        prompt.ShouldContain("afonsoft/demo");
        _audit.Actions.ShouldContain("scm.webhook_triggered");
    }

    [Fact]
    public async Task Webhook_replayed_delivery_is_idempotent()
    {
        var payload = MentionPayload();
        var sig = Sign(payload);
        var first = await _webhook.HandleGitHubAsync(
            sig, "issue_comment", "del-3", payload, CancellationToken.None);
        var second = await _webhook.HandleGitHubAsync(
            sig, "issue_comment", "del-3", payload, CancellationToken.None);
        first.ShouldBe(ScmWebhookResult.Processed);
        second.ShouldBe(ScmWebhookResult.Duplicate);
        _conversations.Calls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Webhook_without_mention_is_ignored()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {"action":"created","installation":{"id":777},
             "repository":{"full_name":"afonsoft/demo"},
             "issue":{"number":4},
             "comment":{"id":100,"body":"no mention here",
               "user":{"login":"afonso"}}}
            """);
        var result = await _webhook.HandleGitHubAsync(
            Sign(payload), "issue_comment", "del-4", payload,
            CancellationToken.None);
        result.ShouldBe(ScmWebhookResult.Ignored);
    }

    [Fact]
    public void Manifest_contains_min_permissions_and_events()
    {
        var manifest = _connections.BuildGitHubAppManifest(
            "https://app.example/api/v1/scm/github/callback", "openhandsnet");
        var json = System.Text.Json.JsonSerializer.Serialize(manifest);
        json.ShouldContain("\"contents\":\"write\"");
        json.ShouldContain("\"pull_requests\":\"write\"");
        json.ShouldContain("\"statuses\":\"write\"");
        json.ShouldContain("issue_comment");
        json.ShouldContain("pull_request_review_comment");
    }

    [Fact]
    public async Task Connection_crud_roundtrip()
    {
        var conn = await _connections.CreateAsync(_orgId, ScmKind.GitHub,
            installationId: "999", pemSecretRef: "p", webhookSecretRef: "w",
            repos: ["afonsoft/demo"], appId: "42", appSlug: "oh",
            createdByUserId: _ownerId.ToString(), CancellationToken.None);
        conn.Kind.ShouldBe(ScmKind.GitHub);
        (await _connections.ListAsync(_orgId, CancellationToken.None))
            .Count.ShouldBe(2);
        (await _connections.FindForRepositoryAsync(
            _orgId, ScmKind.GitHub, "afonsoft/demo",
            CancellationToken.None)).ShouldNotBeNull();
        (await _connections.DeleteAsync(conn.Id, CancellationToken.None))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Pull_request_opens_pr_and_persists_on_conversation()
    {
        var convId = Guid.NewGuid();
        var sandboxId = Guid.NewGuid();
        _db.Conversations.Add(new ConversationRecord
        {
            Id = convId,
            Title = "run",
            OrgId = _orgId,
            SandboxId = sandboxId,
            SelectedRepository = "afonsoft/demo",
            CreatedByUserId = _ownerId.ToString(),
            ExecutionStatus = ConversationExecutionStatus.Running.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();

        var provider = new FakeProvider();
        var registry = new ScmProviderRegistry([provider]);
        var events = new CapturingEvents();
        var svc = new ScmPullRequestService(_db, _connections, registry,
            new FakeSandboxes(sandboxId), new FakeGit(), events, _audit,
            NullLogger<ScmPullRequestService>.Instance);

        var result = await svc.CreateAsync(convId,
            new ScmPrCreateRequest("title", "body", null, "", false, null),
            _ownerId.ToString(), CancellationToken.None);

        result.Error.ShouldBeNull();
        result.PullRequest!.Url.ShouldBe("https://github.com/afonsoft/demo/pull/7");
        var conv = await _db.Conversations.IgnoreQueryFilters()
            .FirstAsync(c => c.Id == convId);
        conv.PullRequestNumber.ShouldBe(7);
        conv.PullRequestHeadSha.ShouldBe("sha-head");
        var appended = events.Appended.ShouldHaveSingleItem();
        appended.ShouldBeOfType<PullRequestCreatedEvent>();
        _audit.Actions.ShouldContain("scm.pr_created");

        // Reexecutar → AlreadyExists (idempotência por PullRequestNumber).
        var again = await svc.CreateAsync(convId,
            new ScmPrCreateRequest("t", "b", null, "", false, null),
            null, CancellationToken.None);
        again.Error.ShouldBe(ScmPrError.AlreadyExists);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private sealed class FakeSecrets : ISecretStore
    {
        public string Value = "hooksecret";

        public Task<string?> ResolveAsync(string scope, string ownerId,
            string name, CancellationToken ct) =>
            Task.FromResult<string?>(Value);

        public Task<IReadOnlyList<string>> ListNamesAsync(string scope,
            string ownerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task SetAsync(string scope, string ownerId, string name,
            string plaintext, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> DeleteAsync(string scope, string ownerId,
            string name, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class CapturingAudit : IAuditService
    {
        public List<string> Actions { get; } = [];

        public Task RecordAsync(string action, string? actorUserId = null,
            Guid? orgId = null, string? targetType = null,
            string? targetId = null, string? ip = null,
            string? userAgent = null, object? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }

        public Task<(IReadOnlyList<AuditEntry> Items, int Total)> QueryAsync(
            string? userId, string? action, DateTimeOffset? from,
            DateTimeOffset? to, int page, int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<AuditEntry>, int)>(([], 0));
    }

    private sealed class CapturingConversations
        : OpenHandsNET.Application.Conversations.IAppConversationService
    {
        public List<(Guid UserId, Guid? OrgId, CreateConversationRequest
            Request)> Calls { get; } = [];

        public Task<AppConversationStartTask> CreateAsync(
            CreateConversationRequest request, CancellationToken ct) =>
            Task.FromResult(Task0());

        public Task<AppConversationStartTask> CreateForUserAsync(Guid userId,
            Guid? orgId, CreateConversationRequest request,
            CancellationToken ct)
        {
            Calls.Add((userId, orgId, request));
            return Task.FromResult(Task0());
        }

        private static AppConversationStartTask Task0() => new(
            Guid.NewGuid(), StartTaskStatus.Working, Guid.NewGuid(),
            null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        public Task<ConversationPage> SearchAsync(ConversationQuery query,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<long> CountAsync(ConversationQuery query,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<IReadOnlyList<AppConversation>> GetBatchAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<AppConversation?> GetAsync(Guid id,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<AppConversation?> UpdateAsync(Guid id, string? title,
            IReadOnlyList<string>? tags, bool? pinned,
            CancellationToken ct) => throw new NotImplementedException();

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

    private sealed class CapturingEvents
        : OpenHandsNET.Application.Events.IEventService
    {
        public List<EventBase> Appended { get; } = [];

        public ValueTask<EventBase> AppendAsync(Guid conversationId,
            EventBase e, CancellationToken ct)
        {
            Appended.Add(e);
            return ValueTask.FromResult(e);
        }

        public Task<IReadOnlyList<EventBase>> GetBatchAsync(
            Guid conversationId, IReadOnlyCollection<string> ids,
            CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<EventPage> SearchAsync(Guid conversationId,
            EventQuery query, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<long> CountAsync(Guid conversationId, EventQuery q,
            CancellationToken ct) => Task.FromResult(0L);

        public async IAsyncEnumerable<EventBase> SubscribeAsync(
            Guid conversationId,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeSandboxes(Guid id)
        : OpenHandsNET.Application.Sandbox.ISandboxService
    {
        public Task<SandboxModel> StartAsync(Guid specId,
            Guid? conversationId, CancellationToken ct) =>
            Task.FromResult(Sbx());

        public Task<SandboxModel> PauseAsync(Guid sandboxId,
            CancellationToken ct) => Task.FromResult(Sbx());

        public Task<SandboxModel> StopAsync(Guid sandboxId,
            CancellationToken ct) => Task.FromResult(Sbx());

        public Task<SandboxModel> ResumeAsync(Guid sandboxId,
            CancellationToken ct) => Task.FromResult(Sbx());

        public Task DeleteAsync(Guid sandboxId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<SandboxModel?> GetAsync(Guid sandboxId,
            CancellationToken ct) =>
            Task.FromResult<SandboxModel?>(Sbx());

        public Task<IReadOnlyList<SandboxModel>> SearchAsync(
            SandboxStatus? status, Guid? conversationId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SandboxModel>>([Sbx()]);

        public Task<SandboxSpec> UpsertSpecAsync(SandboxSpec spec,
            CancellationToken ct) => Task.FromResult(spec);

        public Task<SandboxSpec?> GetSpecAsync(Guid specId,
            CancellationToken ct) =>
            Task.FromResult<SandboxSpec?>(null);

        public Task SweepOrphansAsync(CancellationToken ct) =>
            Task.CompletedTask;

        private SandboxModel Sbx() => new(id, Guid.NewGuid(), null, null,
            SandboxStatus.Running, null, DateTimeOffset.UtcNow, null, null);
    }

    private sealed class FakeGit
        : OpenHandsNET.Application.Git.IGitService
    {
        public Task<GitPushResult> PushAsync(SandboxModel sandbox,
            string head, string? remoteBranch, bool forceWithLease,
            string? ownerId, CancellationToken ct) =>
            Task.FromResult(new GitPushResult(true, "sha-head", null));

        public Task<string?> GetCurrentBranchAsync(SandboxModel sandbox,
            CancellationToken ct) =>
            Task.FromResult<string?>("devin/work");

        public Task<bool> HasRepositoryAsync(SandboxModel sandbox,
            CancellationToken ct) => Task.FromResult(true);

        public Task CloneAsync(SandboxModel sandbox, string url,
            string branch, string? owner, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<bool> ProvisionGhAuthAsync(SandboxModel sandbox,
            string? ownerId, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<GitChanges> GetChangesAsync(SandboxModel sandbox,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<GitDiffReport?> GetDiffAsync(SandboxModel sandbox,
            string? path, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<string?> GetRawDiffAsync(SandboxModel sandbox,
            string? path, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<GitLogEntry>> GetLogAsync(
            SandboxModel sandbox, int limit, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<string?> GetFileAtRefAsync(SandboxModel sandbox,
            string path, string reference, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<GitCommitResult> CommitAsync(SandboxModel sandbox,
            string message, IReadOnlyList<string>? files,
            CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeRegistry : IScmProviderRegistry
    {
        public IScmProvider? Get(ScmKind kind) => new FakeProvider();
    }

    private sealed class FakeProvider : IScmProvider
    {
        public ScmKind Kind => ScmKind.GitHub;

        public Task<ScmPullRequest> CreatePullRequestAsync(
            ScmConnection connection, string repository,
            ScmPrCreateRequest request, CancellationToken ct) =>
            Task.FromResult(new ScmPullRequest(
                "https://github.com/afonsoft/demo/pull/7", 7, "open",
                "sha-head", request.Title));

        public Task<ScmPullRequest?> GetPullRequestAsync(
            ScmConnection connection, string repository, int number,
            CancellationToken ct) =>
            Task.FromResult<ScmPullRequest?>(null);

        public Task<long> CreateIssueCommentAsync(ScmConnection connection,
            string repository, int issueNumber, string body,
            CancellationToken ct) => Task.FromResult(1L);

        public Task<ScmConnectionDiagnostic> TestConnectionAsync(
            ScmConnection connection, CancellationToken ct) =>
            Task.FromResult(new ScmConnectionDiagnostic(
                true, "fake-user", "20.0", 3, "ok"));

        public Task<long> ReplyToReviewCommentAsync(ScmConnection connection,
            string repository, int pullRequestNumber,
            long inReplyToCommentId, string body, CancellationToken ct) =>
            Task.FromResult(1L);

        public Task SetCommitStatusAsync(ScmConnection connection,
            string repository, string sha, string state, string context,
            string? targetUrl, string? description,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
