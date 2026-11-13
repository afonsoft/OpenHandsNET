using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Alerts;
using Sandcastle.Application.ChatOps;
using Sandcastle.Application.Contracts;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Identity;
using Sandcastle.Domain.Conversations;
using Sandcastle.Domain.Events;
using Sandcastle.Infrastructure.Alerts;
using Sandcastle.Infrastructure.ChatOps;
using Sandcastle.Infrastructure.Persistence;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Alerts;

/// <summary>
/// SPEC-151 — signature verifiers (Sentry/PD/generic, fail-closed),
/// normalizers, dedup (delivery + external_id), severity gate, session
/// wiring com agent_type incident-investigator, Teams card/sanitizer.
/// </summary>
public sealed class Spec151AlertIntegrationTests : IAsyncDisposable
{
    private const string Secret = "alert-secret-123";
    private readonly SqliteConnection _conn =
        new("Data Source=:memory:;Default Timeout=10");
    private readonly AppDbContext _db;
    private readonly Guid _orgId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly FakeSecrets _secrets = new();
    private readonly FakeAudit _audit = new();
    private readonly FakeCounter _counter = new();
    private readonly FakeConversations _conversations = new();
    private readonly FakeSink _sink = new();
    private readonly AlertIngestService _ingest;
    private readonly AlertSessionPipeline _sessions;
    private readonly Guid _sourceId;

    public Spec151AlertIntegrationTests()
    {
        _conn.Open();
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
        _db.OrgMembers.Add(new OrgMemberRecord
        {
            OrgId = _orgId,
            UserId = _ownerId,
            Role = "owner",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var options = Options.Create(new AlertOptions
        {
            Enabled = true,
            MaxInboundPerMin = 60,
            MaxSessionsPerHour = 5,
        });
        _sessions = new AlertSessionPipeline(_db, _conversations, _sink,
            _audit, options,
            NullLogger<AlertSessionPipeline>.Instance);
        _ingest = new AlertIngestService(_db, _secrets, _counter,
            _sessions, _audit, options,
            NullLogger<AlertIngestService>.Instance);
        _sourceId = _db.AlertSources.Add(new AlertSourceRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            Provider = "sentry",
            Name = "sentry-prod",
            SecretRef = "alert_sec",
            RepoMappingJson = """{"web-api":"org/web-api"}""",
            Enabled = true,
            AutoCreateSession = true,
            TriageSeverityMin = "high",
            CreatedByUserId = _ownerId.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        }).Entity.Id;
        _db.SaveChanges();
        _secrets.Values[("org", _orgId.ToString(), "alert_sec")] = Secret;
    }

    // ---- signature verifiers (fail-closed) -------------------------

    private static byte[] P(string json) => Encoding.UTF8.GetBytes(json);

    private static string Hmac(string secret, byte[] payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), payload));

    [Fact]
    public void SentrySignature_Valid_Invalid_Missing()
    {
        var payload = P("""{"x":1}""");
        AlertSignatureVerifiers.VerifySentry(
            Secret, Hmac(Secret, payload), payload).ShouldBeTrue();
        AlertSignatureVerifiers.VerifySentry(
            Secret, "deadbeef", payload).ShouldBeFalse();
        AlertSignatureVerifiers.VerifySentry(
            Secret, null, payload).ShouldBeFalse();
    }

    [Fact]
    public void PagerDutySignature_V1Chain_Valid_Invalid_Missing()
    {
        var payload = P("{}");
        var good = Hmac(Secret, payload);
        AlertSignatureVerifiers.VerifyPagerDuty(
            Secret, $"v1={good}", payload).ShouldBeTrue();
        // cadeia p/ rotação — qualquer entrada que case autentica
        AlertSignatureVerifiers.VerifyPagerDuty(
            Secret, $"v1=aaa,v1={good},v1=bbb", payload).ShouldBeTrue();
        AlertSignatureVerifiers.VerifyPagerDuty(
            Secret, "v1=deadbeef", payload).ShouldBeFalse();
        AlertSignatureVerifiers.VerifyPagerDuty(
            Secret, null, payload).ShouldBeFalse();
    }

    [Fact]
    public void GenericSecret_Valid_Invalid_Missing()
    {
        AlertSignatureVerifiers.VerifyGeneric(Secret, Secret)
            .ShouldBeTrue();
        AlertSignatureVerifiers.VerifyGeneric(Secret, "wrong")
            .ShouldBeFalse();
        AlertSignatureVerifiers.VerifyGeneric(Secret, null)
            .ShouldBeFalse();
        // dispatcher: provider errado nunca verifica
        AlertSignatureVerifiers.Verify(AlertProvider.Sentry, null,
            new Dictionary<string, string?>(), P("{}")).ShouldBeFalse();
    }

    // ---- normalizers -------------------------------------------------

    [Fact]
    public void NormalizeSentry_IssuePayload()
    {
        // Sentry internal-integration envelope {action, data:{issue}}.
        using var doc = JsonDocument.Parse("""
        {"action":"created","data":{"issue":{"id":"I-1",
          "title":"NullReference in /pay","culprit":"pay.checkout",
          "level":"error","status":"unresolved",
          "project":{"slug":"web-api"},
          "permalink":"https://sentry.io/issues/I-1"}}}
        """);
        var a = AlertNormalizer.NormalizeSentry(
            doc.RootElement, "del-1")!;
        a.ExternalId.ShouldBe("I-1");
        a.Kind.ShouldBe(AlertEventKinds.Issue);
        a.Severity.ShouldBe(AlertSeverities.High); // error → high
        a.RoutingHint.ShouldBe("web-api");
        a.Culprit.ShouldBe("pay.checkout");
        a.DeliveryId.ShouldBe("del-1");
    }

    [Fact]
    public void NormalizePagerDuty_IncidentTriggeredAndResolved()
    {
        using var doc = JsonDocument.Parse("""
        {"event":{"id":"pd-9","event_type":"incident.resolved",
          "occurred_at":"2026-11-13T10:00:00Z",
          "data":{"id":"INC-9","title":"DB down","urgency":"high",
            "service":{"summary":"web-api"},
            "html_url":"https://pd.example/i/INC-9"}}}
        """);
        var a = AlertNormalizer.NormalizePagerDuty(
            doc.RootElement, null)!;
        a.ExternalId.ShouldBe("INC-9");
        a.Kind.ShouldBe(AlertEventKinds.Resolved);
        a.Severity.ShouldBe(AlertSeverities.High);
        a.Service.ShouldBe("web-api");
    }

    [Fact]
    public void NormalizeGeneric_AndRejectsGarbage()
    {
        using var doc = JsonDocument.Parse("""
        {"id":"g-1","title":"disk full","severity":"critical",
         "service":"nfs","repository":"org/nfs"}
        """);
        var a = AlertNormalizer.NormalizeGeneric(
            doc.RootElement, "d1")!;
        a.Kind.ShouldBe(AlertEventKinds.Issue);
        a.Severity.ShouldBe(AlertSeverities.Critical);
        a.RoutingHint.ShouldBe("org/nfs");
        using var bad = JsonDocument.Parse("""{"foo":1}""");
        AlertNormalizer.NormalizeGeneric(bad.RootElement, null)
            .ShouldBeNull();
        AlertNormalizer.Normalize(AlertProvider.Sentry,
            P("not-json"), null).ShouldBeNull();
    }

    // ---- ingest: signature → dedup → session -------------------------

    private Dictionary<string, string?> SentryHeaders(
        byte[] payload, string? delivery = null)
    {
        var h = new Dictionary<string, string?>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["sentry-hook-signature"] = Hmac(Secret, payload),
        };
        if (delivery is not null)
        {
            h["Sentry-Hook-UUID"] = delivery;
        }
        return h;
    }

    private static byte[] SentryPayload(
        string id = "evt-42", string level = "error",
        string action = "created", string status = "unresolved") =>
        P("""
        {"action":"@a","data":{"issue":{"id":"@i",
         "title":"boom in checkout","culprit":"pay.checkout",
         "level":"@l","status":"@s",
         "project":{"slug":"web-api"},
         "permalink":"https://sentry.io/issues/@i"}}}
        """.Replace("@a", action).Replace("@i", id)
            .Replace("@l", level).Replace("@s", status));

    [Fact]
    public async Task Ingest_NoSignature_401FailClosed()
    {
        var result = await _ingest.HandleAsync(_sourceId,
            new Dictionary<string, string?>(), SentryPayload(),
            CancellationToken.None);
        result.ShouldBe(AlertIngestResult.InvalidSignature);
        _db.AlertEvents.Count().ShouldBe(0); // payload descartado
        _audit.Actions.ShouldContain("alert.webhook_rejected");
    }

    [Fact]
    public async Task Ingest_UnknownSource_404()
    {
        var result = await _ingest.HandleAsync(Guid.NewGuid(),
            new Dictionary<string, string?>(), SentryPayload(),
            CancellationToken.None);
        result.ShouldBe(AlertIngestResult.NoSource);
    }

    [Fact]
    public async Task Ingest_Valid_CreatesEvent_Session_Investigator()
    {
        var payload = SentryPayload();
        var result = await _ingest.HandleAsync(_sourceId,
            SentryHeaders(payload, "del-1"), payload,
            CancellationToken.None);
        result.ShouldBe(AlertIngestResult.Processed);

        var evt = _db.AlertEvents.Single();
        evt.Repository.ShouldBe("org/web-api"); // mapping project→repo
        evt.Status.ShouldBe(AlertEventStatuses.Investigating);
        evt.SessionId.ShouldNotBeNull();
        _conversations.Calls.ShouldHaveSingleItem();
        var (_, _, req) = _conversations.Calls[0];
        req.AgentType.ShouldBe("incident-investigator");
        req.SelectedRepository.ShouldBe("org/web-api");
        _audit.Actions.ShouldContain("alert.event_ingested");
        _audit.Actions.ShouldContain("alert.session_created");
    }

    [Fact]
    public async Task Ingest_Dedup_DeliveryId_And_ExternalId()
    {
        var p1 = SentryPayload("evt-1");
        (await _ingest.HandleAsync(_sourceId,
            SentryHeaders(p1, "del-a"), p1, CancellationToken.None))
            .ShouldBe(AlertIngestResult.Processed);
        // mesma entrega → duplicate antes do evento
        (await _ingest.HandleAsync(_sourceId,
            SentryHeaders(p1, "del-a"), p1, CancellationToken.None))
            .ShouldBe(AlertIngestResult.Duplicate);
        // nova entrega, mesmo external_id → duplicate
        (await _ingest.HandleAsync(_sourceId,
            SentryHeaders(p1, "del-b"), p1, CancellationToken.None))
            .ShouldBe(AlertIngestResult.Duplicate);
        _db.AlertEvents.Count().ShouldBe(1);
        _conversations.Calls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Ingest_Resolved_ClosesOpenEvent()
    {
        var open = SentryPayload("evt-7");
        await _ingest.HandleAsync(_sourceId,
            SentryHeaders(open, "d1"), open, CancellationToken.None);
        var evt = _db.AlertEvents.Single();
        evt.Status.ShouldBe(AlertEventStatuses.Investigating);

        var resolved = SentryPayload(
            "evt-7", action: "resolved", status: "resolved");
        var result = await _ingest.HandleAsync(_sourceId,
            SentryHeaders(resolved, "d2"), resolved,
            CancellationToken.None);
        result.ShouldBe(AlertIngestResult.Processed);
        _db.Entry(evt).Reload();
        evt.Status.ShouldBe(AlertEventStatuses.Resolved);
        _audit.Actions.ShouldContain("alert.event_resolved");
    }

    [Fact]
    public async Task SessionPipeline_SeverityGate_BelowMin_NoSession()
    {
        var source = _db.AlertSources.Single();
        var evt = new AlertEventRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            SourceId = source.Id,
            Provider = "sentry",
            ExternalId = "low-1",
            Kind = "issue",
            Title = "low thing",
            Severity = "low",
            Repository = "org/web-api",
            FirstSeen = DateTimeOffset.UtcNow,
            Status = "new",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.AlertEvents.Add(evt);
        _db.SaveChanges();
        var r = await _sessions.TryCreateAsync(source, evt, null,
            applySeverityGate: true, CancellationToken.None);
        r.Error.ShouldBe(AlertSessionError.BelowSeverity);
        _conversations.Calls.ShouldBeEmpty();
        // gate manual (applySeverityGate: false) cria mesmo assim
        var r2 = await _sessions.TryCreateAsync(source, evt, _ownerId,
            applySeverityGate: false, CancellationToken.None);
        r2.Error.ShouldBeNull();
        evt.SessionId.ShouldNotBeNull();
    }

    [Fact]
    public async Task SessionPipeline_Unrouted_And_Dedup_And_Cap()
    {
        var source = _db.AlertSources.Single();
        var unrouted = new AlertEventRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            SourceId = source.Id,
            Provider = "sentry",
            ExternalId = "u-1",
            Kind = "issue",
            Title = "no mapping",
            Severity = "critical",
            FirstSeen = DateTimeOffset.UtcNow,
            Status = "new",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        (await _sessions.TryCreateAsync(source, unrouted, null,
            true, CancellationToken.None)).Error
            .ShouldBe(AlertSessionError.Unrouted);

        var routed = new AlertEventRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            SourceId = source.Id,
            Provider = "sentry",
            ExternalId = "r-1",
            Kind = "issue",
            Title = "mapped",
            Severity = "critical",
            Repository = "org/web-api",
            FirstSeen = DateTimeOffset.UtcNow,
            Status = "new",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.AlertEvents.Add(routed);
        _db.SaveChanges();
        (await _sessions.TryCreateAsync(source, routed, null,
            true, CancellationToken.None)).Error.ShouldBeNull();
        // segunda tentativa → duplicate
        (await _sessions.TryCreateAsync(source, routed, null,
            true, CancellationToken.None)).Error
            .ShouldBe(AlertSessionError.Duplicate);
        _conversations.Calls.ShouldHaveSingleItem();
    }

    // ---- Teams sink ----------------------------------------------------

    [Fact]
    public void TeamsCard_AdaptiveCardShape()
    {
        var json = JsonSerializer.Serialize(
            TeamsChatSink.BuildCard("hello <at>all</at>", "t"));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("type").GetString()
            .ShouldBe("message");
        var att = doc.RootElement.GetProperty("attachments")[0];
        att.GetProperty("contentType").GetString()
            .ShouldBe("application/vnd.microsoft.card.adaptive");
        att.GetProperty("content").GetProperty("type").GetString()
            .ShouldBe("AdaptiveCard");
    }

    [Fact]
    public void TeamsSanitizer_NeutralizesMentions()
    {
        var s = ChatTextSanitizer.NeutralizeTeamsPings(
            "hey <at>ops</at> @channel @team <at></at> @here");
        s.ShouldNotContain("<at>");
        s.ShouldNotContain("@channel");
        s.ShouldNotContain("@team ");
        s.ShouldNotContain("@here");
        s.ShouldContain("@"); // menção vira @ + zwsp
    }

    [Fact]
    public void SlackSanitizer_BackwardCompatible()
    {
        var s = ChatTextSanitizer.NeutralizeSlackPings(
            "ping @channel and <!subteam^S1> and @here");
        s.ShouldNotContain("@channel");
        s.ShouldNotContain("@here");
        SlackTextSanitizer.NeutralizePings("@everyone")
            .ShouldNotContain("@everyone");
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    // ---- fakes ---------------------------------------------------------

    private sealed class FakeSecrets : ISecretStore
    {
        public Dictionary<(string, string, string), string> Values
        { get; } = [];
        public Task<string?> ResolveAsync(string scope, string ownerId,
            string name, CancellationToken ct) =>
            Task.FromResult(
                Values.TryGetValue((scope, ownerId, name), out var v)
                    ? v : null);
        public Task<IReadOnlyList<string>> ListNamesAsync(
            string scope, string ownerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task SetAsync(string scope, string ownerId, string name,
            string plaintext, CancellationToken ct)
        {
            Values[(scope, ownerId, name)] = plaintext;
            return Task.CompletedTask;
        }
        public Task<bool> DeleteAsync(string scope, string ownerId,
            string name, CancellationToken ct) =>
            Task.FromResult(Values.Remove((scope, ownerId, name)));
    }

    private sealed class FakeCounter
        : Sandcastle.Application.Coordination.IDistributedCounter
    {
        public Task<long> IncrementAsync(string key, TimeSpan ttl,
            CancellationToken ct) => Task.FromResult(1L);
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
        public Task<(IReadOnlyList<AuditEntry> Items, int Total)>
            QueryAsync(string? u, string? a, DateTimeOffset? f,
                DateTimeOffset? t, int p, int ps,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<AuditEntry>, int)>(([], 0));
    }

    private sealed class FakeSink : IChatSink
    {
        public List<string> Posts { get; } = [];
        public Task<string?> PostMessageAsync(ChatConnection c,
            string channel, string? thread, string text,
            CancellationToken ct)
        { Posts.Add(text); return Task.FromResult<string?>("ts1"); }
        public Task UpdateMessageAsync(ChatConnection c, string channel,
            string ts, string text, CancellationToken ct) =>
            Task.CompletedTask;
        public Task AddReactionAsync(ChatConnection c, string channel,
            string ts, string emoji, CancellationToken ct) =>
            Task.CompletedTask;
        public Task SetStatusAsync(ChatConnection c, string channel,
            string threadTs, string status, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeConversations
        : Sandcastle.Application.Conversations.IAppConversationService
    {
        public List<(Guid UserId, Guid? OrgId, CreateConversationRequest
            Request)> Calls
        { get; } = [];
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
        public Task<AppConversation?> SetPermissionsAsync(Guid id,
            IReadOnlyList<Application.Agent.AgentPermissionRule>? rules,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<AppConversation?> SetModeAsync(Guid id, string mode,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<AttentionItem>> ListAttentionAsync(
            int limit, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<AttentionDismissResult> DismissAttentionAsync(
            Guid id, CancellationToken ct) =>
            throw new NotImplementedException();
    }
}