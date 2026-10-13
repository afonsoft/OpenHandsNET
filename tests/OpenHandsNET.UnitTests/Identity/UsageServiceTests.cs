using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Identity;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Infrastructure.Sandbox;
using DomainSandboxStatus = OpenHandsNET.Domain.Sandbox.SandboxStatus;

namespace OpenHandsNET.UnitTests.Identity;

/// <summary>UsageService: org usage aggregation (SPEC-14 RF-005).</summary>
public sealed class UsageServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly UsageService _svc;

    public UsageServiceTests()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"ohn-usage-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path}")
            .UseSnakeCaseNamingConvention().Options);
        _db.Database.Migrate();
        _svc = new UsageService(_db);
        _dbPath = path;
    }

    private readonly string _dbPath;

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private async Task<Guid> SeedOrgAsync()
    {
        var slug = $"org-{Guid.NewGuid():N}"[..20];
        var org = new OrganizationRecord
        {
            Id = Guid.NewGuid(), Name = slug, Slug = slug,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.Organizations.Add(org);
        await _db.SaveChangesAsync();
        return org.Id;
    }

    [Fact]
    public async Task Empty_Org_Returns_Zeros()
    {
        var orgId = await SeedOrgAsync();
        var u = await _svc.GetOrgUsageAsync(orgId, CancellationToken.None);
        u.OrgId.ShouldBe(orgId);
        u.TokensUsed.ShouldBe(0);
        u.SandboxSeconds.ShouldBe(0);
        u.Runs.ShouldBe(0);
        u.SandboxesActive.ShouldBe(0);
    }

    [Fact]
    public async Task Aggregates_Tokens_And_Cost_For_Period()
    {
        var orgId = await SeedOrgAsync();
        var period = DateTimeOffset.UtcNow.ToString("yyyy-MM");
        _db.UsageRecords.AddRange(
            new UsageRecord
            {
                Id = Guid.NewGuid(), OrgId = orgId, Period = period,
                TokensUsed = 1000, CostUsd = 0.5m,
                RecordedAt = DateTimeOffset.UtcNow,
            },
            new UsageRecord
            {
                Id = Guid.NewGuid(), OrgId = orgId, Period = period,
                TokensUsed = 500, CostUsd = 0.25m,
                RecordedAt = DateTimeOffset.UtcNow,
            },
            // other period must not leak in
            new UsageRecord
            {
                Id = Guid.NewGuid(), OrgId = orgId, Period = "1999-01",
                TokensUsed = 999999, RecordedAt = DateTimeOffset.UtcNow,
            });
        await _db.SaveChangesAsync();

        var u = await _svc.GetOrgUsageAsync(orgId, CancellationToken.None);
        u.Period.ShouldBe(period);
        u.TokensUsed.ShouldBe(1500);
        u.CostUsd.ShouldBe(0.75m);
    }

    [Fact]
    public async Task Aggregates_SandboxSeconds_And_Runs()
    {
        var orgId = await SeedOrgAsync();
        var otherOrgId = await SeedOrgAsync();
        var conv = new ConversationRecord
        {
            Id = Guid.NewGuid(), OrgId = orgId,
            ExecutionStatus = "Running",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var otherConv = new ConversationRecord
        {
            Id = Guid.NewGuid(), OrgId = otherOrgId,
            ExecutionStatus = "Running",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.Conversations.AddRange(conv, otherConv);
        _db.Sandboxes.AddRange(
            new SandboxRecord
            {
                Id = Guid.NewGuid(), SpecId = Guid.NewGuid(),
                ConversationId = conv.Id,
                Status = nameof(DomainSandboxStatus.Running),
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            },
            new SandboxRecord
            {
                Id = Guid.NewGuid(), SpecId = Guid.NewGuid(),
                ConversationId = otherConv.Id,
                Status = nameof(DomainSandboxStatus.Running),
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-5),
            });
        await _db.SaveChangesAsync();

        var u = await _svc.GetOrgUsageAsync(orgId, CancellationToken.None);
        u.Runs.ShouldBe(1);
        u.SandboxesActive.ShouldBe(1);
        // ~120 s of sandbox time on OUR org only (the 5h foreign one excluded)
        u.SandboxSeconds.ShouldBeInRange(100, 200);
    }
}
