using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Application.Tickets;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Infrastructure.Tickets;

using Shouldly;

using Xunit;

namespace OpenHandsNET.UnitTests.Tickets;

/// <summary>
/// SPEC-55 — parsers Linear/Jira, trigger eval (label/assignee/
/// state/@menção), start-task + binding, dedup e write-back.
/// </summary>
public sealed class Spec55TicketsTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly Guid _orgId = Guid.NewGuid();
    private readonly FakeConversations _conversations = null!;
    private readonly FakeAudit _audit = new();
    private readonly FakeProvider _linear = new(TicketKind.Linear);
    private readonly FakeProvider _jira = new(TicketKind.Jira);
    private readonly TicketConnectionService _connections;
    private readonly TicketTriggerService _svc;
    private readonly TicketReporter _reporter;
    private readonly Guid _connId;

    public Spec55TicketsTests()
    {
        _conn.Open();
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
        _conversations = new FakeConversations(_db);
        var providers = new Dictionary<TicketKind, ITicketProvider>
        {
            [TicketKind.Linear] = _linear,
            [TicketKind.Jira] = _jira,
        };
        _connections = new TicketConnectionService(_db, _audit);
        _svc = new TicketTriggerService(_db, _connections, providers,
            _conversations, _audit,
            NullLogger<TicketTriggerService>.Instance);
        _reporter = new TicketReporter(_db, providers, _audit,
            NullLogger<TicketReporter>.Instance);
        _db.OrgMembers.Add(new OrgMemberRecord
        {
            OrgId = _orgId,
            UserId = Guid.NewGuid(),
            Role = "owner",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _connId = _db.TicketConnections.Add(new TicketConnectionRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            Kind = "linear",
            ApiUrl = "https://api.linear.app/graphql",
            TokenSecretRef = "tok",
            WebhookSecretRef = "whsec",
            WorkspaceId = "ORG-WS",
            TriggerConfigJson =
                """{"mode":"label","value":"openhandsnet","done_state":"In Review"}""",
            CreatedByUserId = "u1",
            CreatedAt = DateTimeOffset.UtcNow,
        }).Entity.Id;
        _db.SaveChanges();
    }

    private static byte[] P(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void LinearParse_IssueUpdateLabel_ExtractsFields()
    {
        var p = new LinearTicketProvider(
            new FakeHttpFactory(), new FakeSecrets(),
            NullLogger<LinearTicketProvider>.Instance);
        var ev = p.ParseWebhook(P("""
        {
          "type":"Issue","action":"update","webhookId":"w1",
          "createdAt":"2026-10-18T00:00:00Z","organizationId":"ORG-WS",
          "updatedFrom":{"labelIds":["x"]},
          "data":{"identifier":"ENG-42","title":"Fix it",
            "url":"https://linear.app/x/ENG-42",
            "labels":{"nodes":[{"name":"openhandsnet"}]}}
        }
        """));
        ev.ShouldNotBeNull();
        ev!.IssueKey.ShouldBe("ENG-42");
        ev.AddedLabel.ShouldBe("openhandsnet");
        ev.ProjectKey.ShouldBe("ENG");
        ev.DeliveryId.ShouldBe("w1:2026-10-18T00:00:00Z");
    }

    [Fact]
    public void JiraParse_CommentCreated_ExtractsBody()
    {
        var p = new JiraTicketProvider(
            new FakeHttpFactory(), new FakeSecrets(),
            NullLogger<JiraTicketProvider>.Instance);
        var ev = p.ParseWebhook(P("""
        {
          "webhookEvent":"comment_created","timestamp":1700,
          "issue":{"key":"PROJ-7","self":"https://acme.atlassian.net/rest/api/3/issue/1"},
          "comment":{"body":{"type":"doc","content":[
            {"type":"paragraph","content":[
              {"type":"text","text":"@openhandsnet check this"}]}]}}
        }
        """));
        ev.ShouldNotBeNull();
        ev!.EventKind.ShouldBe("comment_created");
        ev.IssueKey.ShouldBe("PROJ-7");
        ev.CommentBody!.ShouldContain("@openhandsnet");
        ev.Url.ShouldBe("https://acme.atlassian.net/browse/PROJ-7");
    }

    [Fact]
    public async Task LabelTrigger_StartsTaskAndBindsTicket()
    {
        _linear.NextEvent = new TicketWebhookEvent(
            "issue_updated", "ENG-42", "d1",
            "openhandsnet", null, null, null, "ENG",
            "Fix flaky test", "desc", "https://l/ENG-42",
            ["openhandsnet"], null, null);
        _linear.VerifyResult = true;
        var r = await _svc.HandleAsync(
            TicketKind.Linear, "sig", "ORG-WS", P("{}"),
            CancellationToken.None);
        r.ShouldBe(TicketOpsResult.Processed);
        var (userId, orgId, req) = _conversations.Calls[0];
        orgId.ShouldBe(_orgId);
        req.InitialMessage!.Content[0].ToString()!
            .ShouldContain("ENG-42");
        req.SelectedRepository.ShouldBeNull();
        var conv = _db.Conversations.IgnoreQueryFilters().Single();
        conv.TicketKind.ShouldBe("linear");
        conv.TicketId.ShouldBe("ENG-42");
        _audit.Actions.ShouldContain("ticket.run_started");
    }

    [Fact]
    public async Task NonMatchingLabel_Ignored()
    {
        _linear.NextEvent = new TicketWebhookEvent(
            "issue_updated", "ENG-42", "d2",
            "other-label", null, null, null, "ENG",
            "t", null, null, ["other-label"], null, null);
        _linear.VerifyResult = true;
        var r = await _svc.HandleAsync(
            TicketKind.Linear, "sig", "ORG-WS", P("{}"),
            CancellationToken.None);
        r.ShouldBe(TicketOpsResult.Ignored);
        _conversations.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task MentionComment_AlwaysTriggers()
    {
        _linear.NextEvent = new TicketWebhookEvent(
            "comment_created", "ENG-9", "d3",
            null, null, null, "@openhandsnet do it", "ENG",
            null, null, "https://l/ENG-9", null, null, null);
        _linear.VerifyResult = true;
        var r = await _svc.HandleAsync(
            TicketKind.Linear, "sig", "ORG-WS", P("{}"),
            CancellationToken.None);
        r.ShouldBe(TicketOpsResult.Processed);
    }

    [Fact]
    public async Task DuplicateDelivery_Skipped()
    {
        _linear.NextEvent = new TicketWebhookEvent(
            "issue_updated", "ENG-42", "dup1",
            "openhandsnet", null, null, null, "ENG",
            "t", null, null, ["openhandsnet"], null, null);
        _linear.VerifyResult = true;
        (await _svc.HandleAsync(TicketKind.Linear, "s", "ORG-WS",
            P("{}"), CancellationToken.None))
            .ShouldBe(TicketOpsResult.Processed);
        (await _svc.HandleAsync(TicketKind.Linear, "s", "ORG-WS",
            P("{}"), CancellationToken.None))
            .ShouldBe(TicketOpsResult.Duplicate);
        _conversations.Calls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task BadSignature_Rejected()
    {
        _linear.VerifyResult = false;
        var r = await _svc.HandleAsync(
            TicketKind.Linear, "bad", "ORG-WS", P("{}"),
            CancellationToken.None);
        r.ShouldBe(TicketOpsResult.InvalidSignature);
    }

    [Fact]
    public async Task RunManual_FetchesIssueAndStarts()
    {
        _linear.NextIssue = new TicketIssue(
            "ENG-42", "Linear task", "desc here",
            "https://l/ENG-42", ["bug"], "To Do", null, "ENG");
        var r = await _svc.RunAsync(_connId, "ENG-42",
            CancellationToken.None);
        r.ShouldBe(TicketOpsResult.Processed);
        _conversations.Calls.ShouldHaveSingleItem();
        var rec = _db.Conversations.IgnoreQueryFilters().Single();
        rec.TicketId.ShouldBe("ENG-42");
    }

    [Fact]
    public async Task Reporter_CommentsAndTransitions()
    {
        var convId = Guid.NewGuid();
        _db.Conversations.Add(
            new OpenHandsNET.Infrastructure.Conversations
                .ConversationRecord
            {
                Id = convId,
                OrgId = _orgId,
                ExecutionStatus = "finished",
                TicketKind = "linear",
                TicketId = "ENG-42",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await _db.SaveChangesAsync();
        await _reporter.ReportRunFinishedAsync(
            convId, true, "done", "https://gh/pr/1",
            CancellationToken.None);
        _linear.Comments.ShouldHaveSingleItem();
        _linear.Transitions.ShouldContain(
            x => x.State == "In Review");
        _audit.Actions.ShouldContain("ticket.writeback");
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private sealed class FakeHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class FakeProvider(TicketKind kind) : ITicketProvider
    {
        public TicketKind Kind => kind;
        public bool VerifyResult { get; set; } = true;
        public TicketWebhookEvent? NextEvent { get; set; }
        public TicketIssue? NextIssue { get; set; }
        public List<(string Key, string Body)> Comments { get; } = [];
        public List<(string Key, string State)> Transitions { get; } = [];

        public Task<bool> VerifyAsync(TicketConnection c,
            string? s, byte[] p, CancellationToken ct) =>
            Task.FromResult(VerifyResult);
        public TicketWebhookEvent? ParseWebhook(byte[] p) => NextEvent;
        public Task<TicketIssue?> GetIssueAsync(TicketConnection c,
            string k, CancellationToken ct) =>
            Task.FromResult(NextIssue);
        public Task AddCommentAsync(TicketConnection c, string k,
            string b, CancellationToken ct)
        { Comments.Add((k, b)); return Task.CompletedTask; }
        public Task TransitionAsync(TicketConnection c, string k,
            string s, CancellationToken ct)
        { Transitions.Add((k, s)); return Task.CompletedTask; }
        public Task<(bool Ok, string? Detail)> TestConnectionAsync(
            TicketConnection c, CancellationToken ct) =>
            Task.FromResult<(bool, string?)>((true, "fake"));
    }

    private sealed class FakeSecrets
        : OpenHandsNET.Application.Contracts.ISecretStore
    {
        public Task<string?> ResolveAsync(string s, string o, string n,
            CancellationToken ct) =>
            Task.FromResult<string?>("secret");
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

    private sealed class FakeConversations(AppDbContext db)
        : OpenHandsNET.Application.Conversations.IAppConversationService
    {
        public List<(Guid UserId, Guid? OrgId, CreateConversationRequest
            Request)> Calls
        { get; } = [];
        public async Task<AppConversationStartTask> CreateAsync(
            CreateConversationRequest r, CancellationToken ct) =>
            await Task0();
        public async Task<AppConversationStartTask> CreateForUserAsync(
            Guid u, Guid? o, CreateConversationRequest r,
            CancellationToken ct)
        { Calls.Add((u, o, r)); return await Task0(); }
        private async Task<AppConversationStartTask> Task0()
        {
            // Espelha o service real: a conversa já existe quando a
            // start-task retorna (binding escreve nela depois).
            var convId = Guid.NewGuid();
            db.Conversations.Add(
                new OpenHandsNET.Infrastructure.Conversations
                    .ConversationRecord
                {
                    Id = convId,
                    ExecutionStatus = "starting",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            await db.SaveChangesAsync();
            return new AppConversationStartTask(
                Guid.NewGuid(), StartTaskStatus.Working, convId,
                null, null, DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
        }
        public Task<ConversationPage> SearchAsync(ConversationQuery q,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<long> CountAsync(ConversationQuery q,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<OpenHandsNET.Domain.Conversations
            .AppConversation>> GetBatchAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<OpenHandsNET.Domain.Conversations.AppConversation?>
            GetAsync(Guid id, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<OpenHandsNET.Domain.Conversations.AppConversation?>
            UpdateAsync(Guid id, string? t,
            IReadOnlyList<string>? tags, bool? p,
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
        public Task<OpenHandsNET.Domain.Conversations.AppConversation?>
            SetPermissionsAsync(Guid id,
            IReadOnlyList<Application.Agent.AgentPermissionRule>? rules,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<OpenHandsNET.Domain.Conversations.AppConversation?>
            SetModeAsync(Guid id, string mode, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<AttentionItem>> ListAttentionAsync(
            int limit, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<AttentionDismissResult> DismissAttentionAsync(
            Guid id, CancellationToken ct) =>
            throw new NotImplementedException();
    }
}