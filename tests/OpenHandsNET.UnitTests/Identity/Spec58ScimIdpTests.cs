using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Infrastructure.Identity;
using OpenHandsNET.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace OpenHandsNET.UnitTests.Identity;

/// <summary>SPEC-58 — scim_* tokens, IdP group sync e usage tiers.</summary>
public sealed class Spec58ScimIdpTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly IdentityAdminService _svc;
    private readonly PlanEnforcementService _enforce;

    public Spec58ScimIdpTests()
    {
        _conn.Open();
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
        _svc = new IdentityAdminService(_db, new FakeAudit());
        _enforce = new PlanEnforcementService(
            _db,
            Options.Create(new BillingOptions { EnforcementEnabled = true }),
            new NullBuffer(),
            NullLogger<PlanEnforcementService>.Instance);
    }

    private sealed class NullBuffer : IUsageBuffer
    {
        public Task AddAsync(
            Guid orgId, long tokens, decimal? costUsd,
            CancellationToken ct) => Task.CompletedTask;

        public Task<PendingUsage> GetPendingAsync(
            Guid orgId, string period, CancellationToken ct) =>
            Task.FromResult(new PendingUsage(orgId, period, 0, 0m));

        public Task<IReadOnlyList<PendingUsage>> SnapshotPendingAsync(
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PendingUsage>>([]);

        public Task ConfirmAsync(
            PendingUsage drained, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeAudit : IAuditService
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
            string? u, string? a, DateTimeOffset? f, DateTimeOffset? t,
            int p, int ps, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<AuditEntry>, int)>(([], 0));
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private Guid SeedUser(string email = "u@x.dev")
    {
        var u = new UserRecord
        {
            Id = Guid.NewGuid(), Email = email,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.Users.Add(u);
        _db.SaveChanges();
        return u.Id;
    }

    [Fact]
    public async Task ScimToken_Mint_Validate_Revoke()
    {
        var issue = await _svc.MintAsync(Org, "okta", default);
        issue.Plaintext.ShouldStartWith("scim_");
        issue.Prefix.ShouldBe(issue.Plaintext[..12]);

        (await _svc.ValidateAsync(issue.Plaintext, default))
            .ShouldBe(Org);
        (await _svc.ValidateAsync("sk-not-scim", default))
            .ShouldBeNull();

        (await _svc.RevokeAsync(Org, issue.Id, default)).ShouldBeTrue();
        (await _svc.ValidateAsync(issue.Plaintext, default))
            .ShouldBeNull();
    }

    [Fact]
    public async Task IdpSync_Creates_Membership_And_Revokes_When_Group_Gone()
    {
        var uid = SeedUser();
        var tier = await _svc.CreateTierAsync(
            Org, "gold", "{\"max_conversations_per_day\":50}", default);
        await _svc.CreateMappingAsync(
            Org, "eng-admins", "admin", tier.Id, default);

        var matched = await _svc.ApplyOnLoginAsync(
            uid, ["eng-admins"], default);
        matched.Count.ShouldBe(1);

        var m = _db.OrgMembers.Single(x => x.UserId == uid);
        m.OrgId.ShouldBe(Org);
        m.Role.ShouldBe("admin");
        m.Source.ShouldBe("idp");
        m.UsageTierId.ShouldBe(tier.Id);

        // idempotente — segundo login sem o grupo revoga a membership.
        await _svc.ApplyOnLoginAsync(uid, [], default);
        _db.OrgMembers.Any(x => x.UserId == uid).ShouldBeFalse();
    }

    [Fact]
    public async Task IdpSync_Manual_Membership_Keeps_Role_But_Gets_Tier()
    {
        var uid = SeedUser();
        var tier = await _svc.CreateTierAsync(
            Org, "gold", "{\"max_conversations_per_day\":50}", default);
        _db.OrgMembers.Add(new OrgMemberRecord
        {
            OrgId = Org, UserId = uid, Role = "owner",
            Source = "manual", CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
        await _svc.CreateMappingAsync(Org, "eng", "member", tier.Id, default);

        await _svc.ApplyOnLoginAsync(uid, ["eng"], default);
        var m = _db.OrgMembers.Single(x => x.UserId == uid);
        m.Role.ShouldBe("owner");          // manual vence
        m.Source.ShouldBe("manual");
        m.UsageTierId.ShouldBe(tier.Id);   // tier ainda aplica
    }

    [Fact]
    public async Task Tier_Resolution_Order_Override_Then_Tier_Then_Plan()
    {
        var uid = SeedUser();
        var plan = new PlanRecord
        {
            Id = Guid.NewGuid(), Name = "pro",
            LimitsJson = "{\"max_conversations_per_day\":10}",
        };
        _db.Plans.Add(plan);
        _db.Organizations.Add(new OrganizationRecord
        {
            Id = Org, Slug = "o", Name = "O", PlanId = plan.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var tier = await _svc.CreateTierAsync(
            Org, "gold", "{\"max_conversations_per_day\":50}", default);
        _db.OrgMembers.Add(new OrgMemberRecord
        {
            OrgId = Org, UserId = uid, Role = "member",
            Source = "idp", UsageTierId = tier.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();

        // tier vence sobre plano
        var eff = await _enforce.GetEffectiveLimitsAsync(Org, uid, default);
        eff.Limits.MaxConversationsPerDay.ShouldBe(50);

        // override vence sobre tier
        await _svc.SetUserOverrideAsync(
            Org, uid, Guid.NewGuid(),
            "{\"max_conversations_per_day\":99}", null, default);
        eff = await _enforce.GetEffectiveLimitsAsync(Org, uid, default);
        eff.Name.ShouldBe("override");
        eff.Limits.MaxConversationsPerDay.ShouldBe(99);

        // override expirado → volta pro tier
        await _svc.SetUserOverrideAsync(
            Org, uid, Guid.NewGuid(),
            "{\"max_conversations_per_day\":1}",
            DateTimeOffset.UtcNow.AddHours(-1), default);
        eff = await _enforce.GetEffectiveLimitsAsync(Org, uid, default);
        eff.Limits.MaxConversationsPerDay.ShouldBe(50);
    }

    [Fact]
    public async Task Admin_Crud_Mappings_And_Tiers()
    {
        var t = await _svc.CreateTierAsync(Org, "t1", null, default);
        (await _svc.ListTiersAsync(Org, default)).Count.ShouldBe(1);
        (await _svc.UpdateTierAsync(
            Org, t.Id, "t2", "{}", default))!.Name.ShouldBe("t2");

        var m = await _svc.CreateMappingAsync(
            Org, "g1", "member", t.Id, default);
        (await _svc.ListMappingsAsync(Org, default))
            .Single().IdpGroup.ShouldBe("g1");

        await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.CreateMappingAsync(Org, "g2", "boss", null, default));

        (await _svc.DeleteMappingAsync(Org, m.Id, default)).ShouldBeTrue();
        (await _svc.DeleteTierAsync(Org, t.Id, default)).ShouldBeTrue();
    }
}
