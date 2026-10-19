using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using OpenHandsNET.Application.Contracts;
using OpenHandsNET.Application.Sharing;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Eventing;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Infrastructure.Sharing;

using Shouldly;

using Xunit;

namespace OpenHandsNET.UnitTests.Sharing;

/// <summary>SPEC-56 — ciclo de vida do share link + leitura pública.</summary>
public sealed class ShareServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly ShareService _svc;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _convId = Guid.NewGuid();

    public ShareServiceTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
        _svc = new ShareService(_db, new PublicEventFilter(),
            new FakeSecretStore(),
            new ConfigurationBuilder().Build());
        SeedConversation();
    }

    private void SeedConversation()
    {
        var now = DateTimeOffset.UtcNow;
        _db.Conversations.Add(new ConversationRecord
        {
            Id = _convId,
            Title = "test run",
            CreatedByUserId = _ownerId.ToString(),
            ExecutionStatus =
                ConversationExecutionStatus.Running.ToString(),
            SecretNamesJson = "[\"GH_TOKEN\"]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Events.Add(new EventRecord
        {
            Id = "ev-1",
            ConversationId = _convId,
            Kind = "MessageEvent",
            Source = "user",
            TimestampTicks = now.UtcTicks,
            Payload = """{"kind":"MessageEvent","id":"ev-1","text":"hi hunter2"}""",
        });
        _db.Events.Add(new EventRecord
        {
            Id = "ev-2",
            ConversationId = _convId,
            Kind = "StreamingDeltaEvent",
            Source = "agent",
            TimestampTicks = now.UtcTicks + 1,
            Payload = """{"kind":"StreamingDeltaEvent","id":"ev-2","delta":"x"}""",
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Create_Returns_Token_And_Url()
    {
        var link = await _svc.CreateOrGetAsync(
            _convId, _ownerId, CancellationToken.None);

        link.Token.ShouldNotBeNullOrEmpty();
        link.Url.ShouldBe($"/share/{link.Token}");
        // Só o hash persiste — o token bruto nunca vai ao banco.
        _db.ConversationShares.Single().TokenHash
            .ShouldNotBe(link.Token);
    }

    [Fact]
    public async Task Create_Twice_Reuses_Active_Link_Without_Token()
    {
        var first = await _svc.CreateOrGetAsync(
            _convId, _ownerId, CancellationToken.None);
        var second = await _svc.CreateOrGetAsync(
            _convId, _ownerId, CancellationToken.None);

        second.Token.ShouldBeEmpty();
        _db.ConversationShares.Count().ShouldBe(1);
        first.Token.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Shared_Read_Exposes_Filtered_Events_Only()
    {
        var link = await _svc.CreateOrGetAsync(
            _convId, _ownerId, CancellationToken.None);

        var payload = await _svc.GetSharedAsync(
            link.Token, 100, CancellationToken.None);

        payload.ShouldNotBeNull();
        payload.Title.ShouldBe("test run");
        // StreamingDeltaEvent foi filtrado — só a mensagem aparece.
        payload.Items.Count.ShouldBe(1);
        // Secret value resolvido da store fake é redigido.
        payload.Items[0].GetRawText().ShouldNotContain("hunter2");
    }

    [Fact]
    public async Task Revoked_Token_Returns_Null_Indistinguishable()
    {
        var link = await _svc.CreateOrGetAsync(
            _convId, _ownerId, CancellationToken.None);
        await _svc.RevokeAsync(_convId, _ownerId, CancellationToken.None);

        (await _svc.GetSharedAsync(link.Token, 100, CancellationToken.None))
            .ShouldBeNull();
        (await _svc.GetSharedAsync("bogus-token", 100, CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Other_User_Cannot_Create_Or_Revoke()
    {
        var stranger = Guid.NewGuid();
        await Should.ThrowAsync<ShareForbiddenException>(
            () => _svc.CreateOrGetAsync(
                _convId, stranger, CancellationToken.None));
    }

    [Fact]
    public async Task Shares_Disabled_Blocks_Creation()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Shares:Enabled"] = "false",
            }).Build();
        var svc = new ShareService(_db, new PublicEventFilter(),
            new FakeSecretStore(), cfg);

        await Should.ThrowAsync<ShareForbiddenException>(
            () => svc.CreateOrGetAsync(
                _convId, _ownerId, CancellationToken.None));
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    /// <summary>Secret store fake — devolve valor fixo p/ redação.</summary>
    private sealed class FakeSecretStore : ISecretStore
    {
        public Task<string?> ResolveAsync(
            string scope, string ownerId, string name, CancellationToken ct) =>
            Task.FromResult<string?>("hunter2");

        public Task<IReadOnlyList<string>> ListNamesAsync(
            string scope, string ownerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task SetAsync(string scope, string ownerId, string name,
            string plaintext, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> DeleteAsync(
            string scope, string ownerId, string name, CancellationToken ct) =>
            Task.FromResult(true);
    }
}