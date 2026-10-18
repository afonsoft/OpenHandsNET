using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Infrastructure.Identity;
using OpenHandsNET.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace OpenHandsNET.UnitTests.Identity;

/// <summary>SPEC-57: service users, chaves svc_* escopadas e revogação.</summary>
public sealed class Spec57ServiceUsersTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly Guid _orgId = Guid.NewGuid();
    private readonly ServiceUserService _svc;
    private readonly ApiKeyService _keys;

    public Spec57ServiceUsersTests()
    {
        _conn.Open();
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
        _svc = new ServiceUserService(_db);
        var scopeFactory = new ServiceCollection()
            .AddSingleton(_db).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        _keys = new ApiKeyService(
            _db, scopeFactory,
            NullLogger<ApiKeyService>.Instance);
    }

    [Fact]
    public async Task Create_List_Update_Flow()
    {
        var created = await _svc.CreateAsync(_orgId,
            new ServiceUserCreateRequest("ci-bot", "member"),
            Guid.NewGuid(), CancellationToken.None);
        created.Role.ShouldBe("member");
        created.DisabledAt.ShouldBeNull();

        (await _svc.ListAsync(_orgId, CancellationToken.None))
            .Single().Id.ShouldBe(created.Id);

        var updated = await _svc.UpdateAsync(_orgId, created.Id,
            new ServiceUserUpdateRequest(null, "admin", null),
            CancellationToken.None);
        updated!.Role.ShouldBe("admin");

        var disabled = await _svc.UpdateAsync(_orgId, created.Id,
            new ServiceUserUpdateRequest(null, null, true),
            CancellationToken.None);
        disabled!.DisabledAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Create_RejectsBadRole()
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            _svc.CreateAsync(_orgId,
                new ServiceUserCreateRequest("x", "superadmin"),
                Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task KeyIssue_SvcPrefix_ScopesPersisted()
    {
        var svc = await _svc.CreateAsync(_orgId,
            new ServiceUserCreateRequest("ci-bot", "member"),
            Guid.NewGuid(), CancellationToken.None);
        var key = await _svc.CreateKeyAsync(_orgId, svc.Id,
            new ServiceUserKeyCreateRequest(
                "deploy", ["conversations:write"], null),
            CancellationToken.None);

        key.Plaintext.ShouldStartWith("svc_");
        key.Key.Prefix.ShouldStartWith("svc_");

        var resolution = await _keys.ValidateAsync(
            key.Plaintext, CancellationToken.None);
        resolution.ShouldNotBeNull();
        resolution!.ActorKind.ShouldBe(ActorKind.ServiceUser);
        resolution.ServiceUserId.ShouldBe(svc.Id);
        resolution.Role.ShouldBe("member");
        resolution.Scopes.ShouldBe(["conversations:write"]);
    }

    [Fact]
    public async Task KeyIssue_RejectsUnknownScope()
    {
        var svc = await _svc.CreateAsync(_orgId,
            new ServiceUserCreateRequest("ci-bot", "member"),
            Guid.NewGuid(), CancellationToken.None);
        var e = await Should.ThrowAsync<ArgumentException>(() =>
            _svc.CreateKeyAsync(_orgId, svc.Id,
                new ServiceUserKeyCreateRequest(
                    null, ["bogus:scope"], null),
                CancellationToken.None));
        e.Message.ShouldContain("unknown scopes");
    }

    [Fact]
    public async Task DisabledServiceUser_RevokesKeysImmediately()
    {
        var svc = await _svc.CreateAsync(_orgId,
            new ServiceUserCreateRequest("ci-bot", "member"),
            Guid.NewGuid(), CancellationToken.None);
        var key = await _svc.CreateKeyAsync(_orgId, svc.Id,
            new ServiceUserKeyCreateRequest(
                null, ["conversations:write"], null),
            CancellationToken.None);

        await _svc.UpdateAsync(_orgId, svc.Id,
            new ServiceUserUpdateRequest(null, null, true),
            CancellationToken.None);

        (await _keys.ValidateAsync(
            key.Plaintext, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Delete_RemovesKeysToo()
    {
        var svc = await _svc.CreateAsync(_orgId,
            new ServiceUserCreateRequest("ci-bot", "member"),
            Guid.NewGuid(), CancellationToken.None);
        var key = await _svc.CreateKeyAsync(_orgId, svc.Id,
            new ServiceUserKeyCreateRequest(null, ["conversations:write"],
                null), CancellationToken.None);

        (await _svc.DeleteAsync(_orgId, svc.Id,
            CancellationToken.None)).ShouldBeTrue();
        (await _keys.ValidateAsync(key.Plaintext,
            CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task PersonalSkKey_KeepsActorKindUser()
    {
        var userId = Guid.NewGuid();
        _db.Users.Add(new UserRecord
        {
            Id = userId, Email = "u@t.com", CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();

        var created = await _keys.CreateAsync(
            new AuthUser(userId, "u@t.com", null, false, null,
                AuthType.Bearer),
            null, null, null, _orgId, CancellationToken.None);
        var resolution = await _keys.ValidateAsync(
            created.Plaintext, CancellationToken.None);
        resolution.ShouldNotBeNull();
        resolution!.ActorKind.ShouldBe(ActorKind.User);
        resolution.Scopes.ShouldBeNull();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
