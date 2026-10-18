using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.ChatOps;
using OpenHandsNET.Application.Contracts;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.ChatOps;
using OpenHandsNET.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace OpenHandsNET.UnitTests.ChatOps;

/// <summary>
/// SPEC-54 — signature v0 Slack, url_verification, app_mention →
/// start-task + thread binding, reply → send-message, slash command,
/// auto-triage flag.
/// </summary>
public sealed class Spec54ChatOpsTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly Guid _orgId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly FakeConversations _conversations = new();
    private readonly FakeAudit _audit = new();
    private readonly FakeSecrets _secrets = new();
    private readonly FakeSink _sink = new();
    private readonly FakeEvents _events = new();
    private readonly SlackChatOpsService _svc;
    private readonly ChatConnectionService _connections;
    private Guid _connId;

    public Spec54ChatOpsTests()
    {
        _conn.Open();
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
        _connections = new ChatConnectionService(_db, _audit);
        var scopeFactory = new ServiceCollection()
            .AddSingleton(_db).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        _svc = new SlackChatOpsService(_db, _secrets, _connections,
            _conversations, _events, new FakeRunnerHost(), _sink, _audit,
            scopeFactory, NullLogger<SlackChatOpsService>.Instance);
        _db.OrgMembers.Add(new OrgMemberRecord
        {
            OrgId = _orgId, UserId = _ownerId, Role = "owner",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
        _connId = _db.ChatConnections.Add(new ChatConnectionRecord
        {
            Id = Guid.NewGuid(), OrgId = _orgId, Kind = "slack",
            TeamId = "T123", BotTokenSecretRef = "bot_tok",
            SigningSecretRef = "sign_sec", BotUserId = "UBOT",
            CreatedByUserId = _ownerId.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        }).Entity.Id;
        _db.SaveChanges();
    }

    private string Sig(byte[] payload, string ts)
    {
        var baseStr = $"v0:{ts}:{Encoding.UTF8.GetString(payload)}";
        return "v0=" + Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("slack-signing"),
            Encoding.UTF8.GetBytes(baseStr)));
    }

    private static byte[] P(string json) => Encoding.UTF8.GetBytes(json);

    private static string Now() =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

    [Fact]
    public void VerifySignature_AcceptsValid_RejectsStaleAndBad()
    {
        var ts = Now();
        var payload = P("{}");
        SlackChatOpsService.VerifySignature(
            "slack-signing", ts, Sig(payload, ts), payload,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ShouldBeTrue();
        SlackChatOpsService.VerifySignature(
            "slack-signing", "100", Sig(payload, "100"), payload,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ShouldBeFalse();
        SlackChatOpsService.VerifySignature(
            "slack-signing", ts, "v0=deadbeef", payload,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ShouldBeFalse();
    }

    [Fact]
    public async Task UrlVerification_ReturnsChallenge()
    {
        var payload = P(
            """{"type":"url_verification","team_id":"T123","challenge":"abc42"}""");
        var ts = Now();
        var (result, challenge) = await _svc.HandleEventAsync(
            Sig(payload, ts), ts, payload, CancellationToken.None);
        result.ShouldBe(ChatOpsResult.Challenge);
        challenge.ShouldBe("abc42");
    }

    [Fact]
    public async Task BadSignature_Rejected()
    {
        var payload = P("""{"team_id":"T123"}""");
        var (result, _) = await _svc.HandleEventAsync(
            "v0=nope", Now(), payload, CancellationToken.None);
        result.ShouldBe(ChatOpsResult.InvalidSignature);
    }

    [Fact]
    public async Task AppMention_CreatesConversationAndBinding()
    {
        var payload = P("""
        {
          "team_id":"T123","event_id":"Ev1",
          "event":{"type":"app_mention","channel":"C1","ts":"1700.01",
            "text":"<@UBOT> fix the flaky test","user":"U1"}
        }
        """);
        var ts = Now();
        var (result, _) = await _svc.HandleEventAsync(
            Sig(payload, ts), ts, payload, CancellationToken.None);
        result.ShouldBe(ChatOpsResult.Processed);
        _conversations.Calls.ShouldHaveSingleItem();
        _db.ChatThreadBindings.Count().ShouldBe(1);
        // Postback é fire-and-forget — espera a reação chegar.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_sink.Reactions.Count == 0
            && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
        _sink.Reactions.ShouldContain(r => r.Contains("eyes"));
        _audit.Actions.ShouldContain("chatops.thread_bound");
    }

    [Fact]
    public async Task DuplicateEventId_Skipped()
    {
        var payload = P("""
        {"team_id":"T123","event_id":"Ev2",
         "event":{"type":"app_mention","channel":"C1","ts":"1.1",
           "text":"<@UBOT> x","user":"U1"}}
        """);
        var ts = Now();
        var sig = Sig(payload, ts);
        (await _svc.HandleEventAsync(sig, ts, payload,
            CancellationToken.None)).Result
            .ShouldBe(ChatOpsResult.Processed);
        (await _svc.HandleEventAsync(sig, ts, payload,
            CancellationToken.None)).Result
            .ShouldBe(ChatOpsResult.Duplicate);
        _conversations.Calls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ThreadReply_RoutesToBoundConversation()
    {
        var convId = Guid.NewGuid();
        _db.ChatThreadBindings.Add(new ChatThreadBindingRecord
        {
            Id = Guid.NewGuid(), ConnectionId = _connId,
            ChannelId = "C1", ThreadTs = "1700.99",
            ConversationId = convId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
        var payload = P("""
        {"team_id":"T123","event_id":"Ev3",
         "event":{"type":"message","channel":"C1","ts":"1701.00",
           "thread_ts":"1700.99","text":"more info","user":"U1"}}
        """);
        var ts = Now();
        var (result, _) = await _svc.HandleEventAsync(
            Sig(payload, ts), ts, payload, CancellationToken.None);
        result.ShouldBe(ChatOpsResult.Processed);
        _conversations.Calls.ShouldBeEmpty();
        _events.Appended.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AutoTriage_ChannelMessage_StartsSession()
    {
        await _connections.SetChannelConfigAsync(_connId, "C9",
            "org/repo", null, autoTriage: true, CancellationToken.None);
        var payload = P("""
        {"team_id":"T123","event_id":"Ev4",
         "event":{"type":"message","channel":"C9","ts":"1702.1",
           "text":"the deploy is broken","user":"U2"}}
        """);
        var ts = Now();
        var (result, _) = await _svc.HandleEventAsync(
            Sig(payload, ts), ts, payload, CancellationToken.None);
        result.ShouldBe(ChatOpsResult.Processed);
        _conversations.Calls.ShouldHaveSingleItem();
        _conversations.Calls[0].Request.SelectedRepository
            .ShouldBe("org/repo");
    }

    [Fact]
    public async Task SlashCommand_CreatesSession()
    {
        var formStr = "team_id=T123&channel_id=C5&user_id=U9&" +
            "text=investigate%20the%20bug";
        var raw = Encoding.UTF8.GetBytes(formStr);
        var form = new Dictionary<string, string>
        {
            ["team_id"] = "T123", ["channel_id"] = "C5",
            ["user_id"] = "U9", ["text"] = "investigate the bug",
        };
        var ts = Now();
        var (result, reply) = await _svc.HandleCommandAsync(
            Sig(raw, ts), ts, raw, form, CancellationToken.None);
        result.ShouldBe(ChatOpsResult.Processed);
        reply!.ShouldContain("Sessão criada");
        _conversations.Calls.ShouldHaveSingleItem();
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private sealed class FakeSecrets : ISecretStore
    {
        public Task<string?> ResolveAsync(string s, string o, string n,
            CancellationToken ct) =>
            Task.FromResult<string?>(n == "sign_sec"
                ? "slack-signing" : "xoxb-fake");
        public Task<IReadOnlyList<string>> ListNamesAsync(string s,
            string o, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task SetAsync(string s, string o, string n, string v,
            CancellationToken ct) => Task.CompletedTask;
        public Task<bool> DeleteAsync(string s, string o, string n,
            CancellationToken ct) => Task.FromResult(true);
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

    private sealed class FakeSink : IChatSink
    {
        public List<string> Reactions { get; } = [];
        public List<string> Messages { get; } = [];
        public Task<string?> PostMessageAsync(ChatConnection c,
            string ch, string? th, string text, CancellationToken ct)
        { Messages.Add(text); return Task.FromResult<string?>("999.1"); }
        public Task UpdateMessageAsync(ChatConnection c, string ch,
            string ts, string text, CancellationToken ct) =>
            Task.CompletedTask;
        public Task AddReactionAsync(ChatConnection c, string ch,
            string ts, string emoji, CancellationToken ct)
        { Reactions.Add(emoji); return Task.CompletedTask; }
        public Task SetStatusAsync(ChatConnection c, string ch,
            string th, string s, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeEvents
        : OpenHandsNET.Application.Events.IEventService
    {
        public List<EventBase> Appended { get; } = [];
        public ValueTask<EventBase> AppendAsync(Guid c, EventBase e,
            CancellationToken ct)
        { Appended.Add(e); return ValueTask.FromResult(e); }
        public Task<IReadOnlyList<EventBase>> GetBatchAsync(Guid c,
            IReadOnlyCollection<string> ids, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<OpenHandsNET.Application.Events.EventPage> SearchAsync(
            Guid c, OpenHandsNET.Application.Events.EventQuery q,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<long> CountAsync(Guid c,
            OpenHandsNET.Application.Events.EventQuery q,
            CancellationToken ct) => Task.FromResult(0L);
        public async IAsyncEnumerable<EventBase> SubscribeAsync(Guid c,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct)
        { await Task.CompletedTask; yield break; }
    }

    private sealed class FakeRunnerHost : IAgentRunnerHost
    {
        public Task NotifyMessageAsync(Guid conversationId,
            CancellationToken ct) => Task.CompletedTask;
        public Task ConfirmActionAsync(Guid conversationId,
            string actionId, bool approved, string? reason,
            CancellationToken ct) => Task.CompletedTask;
        public Task InterruptAsync(Guid conversationId,
            CancellationToken ct) => Task.CompletedTask;
        public Task PauseAsync(Guid conversationId,
            CancellationToken ct) => Task.CompletedTask;
        public Task ResumeAsync(Guid conversationId,
            CancellationToken ct) => Task.CompletedTask;
        public bool IsRunning(Guid conversationId) => false;
    }

    private sealed class FakeConversations
        : OpenHandsNET.Application.Conversations.IAppConversationService
    {
        public List<(Guid UserId, Guid? OrgId, CreateConversationRequest
            Request)> Calls { get; } = [];
        public Task<AppConversationStartTask> CreateAsync(
            CreateConversationRequest r, CancellationToken ct) =>
            Task.FromResult(Task0());
        public Task<AppConversationStartTask> CreateForUserAsync(
            Guid u, Guid? o, CreateConversationRequest r,
            CancellationToken ct)
        { Calls.Add((u, o, r)); return Task.FromResult(Task0()); }
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
