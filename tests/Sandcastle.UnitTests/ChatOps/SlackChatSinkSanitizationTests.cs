using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.ChatOps;
using Sandcastle.Application.Contracts;
using Sandcastle.Infrastructure.ChatOps;
using Sandcastle.Infrastructure.Persistence;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.ChatOps;

/// <summary>
/// SPEC-147 RF-001 — o <see cref="SlackChatSink"/> é o ponto único de
/// saída: o texto enviado ao Slack sai com pings de grupo neutralizados
/// (default), intactos com opt-in da org ou da instância.
/// </summary>
public sealed class SlackChatSinkSanitizationTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly CapturingHandler _http = new();
    private readonly Guid _orgId = Guid.NewGuid();

    public SlackChatSinkSanitizationTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
        _db.Organizations.Add(new OrganizationRecord
        {
            Id = _orgId,
            Slug = "acme",
            Name = "Acme",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
    }

    private SlackChatSink Sink(bool allowGlobal = false) => new(
        new FakeHttpFactory(_http), new FakeSecrets(), _db,
        Options.Create(new ChatOpsOptions
        {
            AllowGroupMentions = allowGlobal,
        }),
        NullLogger<SlackChatSink>.Instance);

    private ChatConnection Connection(Guid? orgId) => new(
        Guid.NewGuid(), orgId, ChatKind.Slack, "T1",
        "bot", "sign", null, null, null, DateTimeOffset.UtcNow);

    /// <summary>Campo <c>text</c> do último corpo JSON postado.</summary>
    private string SentText() =>
        JsonDocument.Parse(_http.LastBody!)
            .RootElement.GetProperty("text").GetString()!;

    [Fact]
    public async Task PostMessage_GroupPing_NeutralizedByDefault()
    {
        await Sink().PostMessageAsync(Connection(_orgId), "C1", null,
            "Done @channel — see <!here>", CancellationToken.None);

        var text = SentText();
        text.ShouldContain("@\u200Bchannel");
        text.ShouldContain("@\u200Bhere");
        text.ShouldNotContain("@channel");
        text.ShouldNotContain("<!here>");
    }

    [Fact]
    public async Task UpdateMessage_GroupPing_NeutralizedByDefault()
    {
        await Sink().UpdateMessageAsync(Connection(_orgId), "C1", "1.2",
            "fail <!everyone>", CancellationToken.None);

        var text = SentText();
        text.ShouldContain("@\u200Beveryone");
        text.ShouldNotContain("<!everyone>");
    }

    [Fact]
    public async Task PostMessage_UserMentions_Preserved()
    {
        await Sink().PostMessageAsync(Connection(_orgId), "C1", null,
            "cc <@U123>", CancellationToken.None);

        SentText().ShouldContain("<@U123>");
    }

    [Fact]
    public async Task PostMessage_OrgOptIn_AllowsPings()
    {
        var org = await _db.Organizations.FindAsync(_orgId);
        org!.SettingsJson = JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                [SlackChatSink.OrgSettingAllowGroupMentions] = "true",
            });
        await _db.SaveChangesAsync();

        await Sink().PostMessageAsync(Connection(_orgId), "C1", null,
            "hey @channel", CancellationToken.None);

        SentText().ShouldBe("hey @channel");
    }

    [Fact]
    public async Task PostMessage_GlobalOptIn_AllowsPings()
    {
        await Sink(allowGlobal: true).PostMessageAsync(
            Connection(null), "C1", null, "hey @channel",
            CancellationToken.None);

        SentText().ShouldBe("hey @channel");
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"ok":true,"ts":"1.2"}""", Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class FakeHttpFactory(HttpMessageHandler handler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class FakeSecrets : ISecretStore
    {
        public Task<string?> ResolveAsync(string s, string o, string n,
            CancellationToken ct) => Task.FromResult<string?>("xoxb-fake");
        public Task<IReadOnlyList<string>> ListNamesAsync(string s,
            string o, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task SetAsync(string s, string o, string n, string v,
            CancellationToken ct) => Task.CompletedTask;
        public Task<bool> DeleteAsync(string s, string o, string n,
            CancellationToken ct) => Task.FromResult(true);
    }
}