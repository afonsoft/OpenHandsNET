using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Identity;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Identity;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Infrastructure.Sandbox;

using DomainSandboxStatus = OpenHandsNET.Domain.Sandbox.SandboxStatus;

namespace OpenHandsNET.UnitTests.Identity;

/// <summary>
/// SPEC-20: PlanLimits parse, effective-plan resolution, enforcement gates,
/// usage metering write-path e plan CRUD/assignment.
/// </summary>
public sealed class PlanEnforcementTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDbContext _db;

    public PlanEnforcementTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(), $"ohn-plan-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .UseSnakeCaseNamingConvention().Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private PlanEnforcementService Enforcement(bool enabled = true) => new(
        _db, Options.Create(new BillingOptions { EnforcementEnabled = enabled }),
        new OpenHandsNET.Application.Identity.NullUsageBuffer(),
        NullLogger<PlanEnforcementService>.Instance);

    private async Task<Guid> SeedOrgAsync(string? limitsJson = null)
    {
        var slug = $"org-{Guid.NewGuid():N}"[..20];
        var org = new OrganizationRecord
        {
            Id = Guid.NewGuid(),
            Name = slug,
            Slug = slug,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.Organizations.Add(org);
        if (limitsJson is not null)
        {
            var plan = new PlanRecord
            {
                Id = Guid.NewGuid(),
                Name = $"p-{Guid.NewGuid():N}"[..16],
                LimitsJson = limitsJson,
            };
            _db.Plans.Add(plan);
            org.PlanId = plan.Id;
        }

        await _db.SaveChangesAsync();
        return org.Id;
    }

    private ConversationRecord NewConversation(
        Guid? orgId, string status = "Stopped") => new()
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            ExecutionStatus = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    // ---------- PlanLimits.Parse ----------

    [Fact]
    public void Parse_Valid_Json_Reads_All_Limits()
    {
        var l = PlanLimits.Parse("""
            {
              "max_conversations_per_day": 10,
              "max_concurrent_runs": 3,
              "max_tokens_per_month": 500000,
              "max_sandbox_seconds_per_month": 7200,
              "max_sandbox_profile": "highcpu"
            }
            """);
        l.MaxConversationsPerDay.ShouldBe(10);
        l.MaxConcurrentRuns.ShouldBe(3);
        l.MaxTokensPerMonth.ShouldBe(500000);
        l.MaxSandboxSecondsPerMonth.ShouldBe(7200);
        l.MaxSandboxProfile.ShouldBe("highcpu");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json {")]
    [InlineData("[1,2,3]")]
    public void Parse_Malformed_Or_Empty_Fails_Open(string? json)
    {
        PlanLimits.Parse(json).ShouldBe(PlanLimits.Empty);
    }

    [Fact]
    public void Parse_Negative_Values_Treated_As_Unset()
    {
        var l = PlanLimits.Parse(
            """{"max_conversations_per_day": -1, "max_concurrent_runs": 2}""");
        l.MaxConversationsPerDay.ShouldBeNull();
        l.MaxConcurrentRuns.ShouldBe(2);
    }

    [Fact]
    public void ToJson_Roundtrips_Canonical_Shape()
    {
        var l = new PlanLimits(MaxConversationsPerDay: 5);
        var round = PlanLimits.Parse(l.ToJson());
        round.MaxConversationsPerDay.ShouldBe(5);
        round.MaxConcurrentRuns.ShouldBeNull();
    }

    // ---------- GetEffectivePlanAsync ----------

    [Fact]
    public async Task EffectivePlan_No_Org_Falls_Back_To_Free()
    {
        _db.Plans.Add(new PlanRecord
        {
            Id = Guid.NewGuid(),
            Name = "free",
            LimitsJson = "{}",
        });
        await _db.SaveChangesAsync();
        var p = await Enforcement().GetEffectivePlanAsync(
            null, CancellationToken.None);
        p.Name.ShouldBe("free");
        p.Limits.ShouldBe(PlanLimits.Empty);
    }

    [Fact]
    public async Task EffectivePlan_No_Free_Plan_Still_Fails_Open()
    {
        var p = await Enforcement().GetEffectivePlanAsync(
            Guid.NewGuid(), CancellationToken.None);
        p.Name.ShouldBe("free");
        p.PlanId.ShouldBeNull();
        p.Limits.ShouldBe(PlanLimits.Empty);
    }

    [Fact]
    public async Task EffectivePlan_Active_Subscription_Wins_Over_OrgPlan()
    {
        var orgId = await SeedOrgAsync("""{"max_concurrent_runs": 1}""");
        var subPlan = new PlanRecord
        {
            Id = Guid.NewGuid(),
            Name = "team",
            LimitsJson = """{"max_concurrent_runs": 9}""",
        };
        _db.Plans.Add(subPlan);
        _db.Subscriptions.Add(new SubscriptionRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            PlanId = subPlan.Id,
            Status = "active",
            StartedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
        var p = await Enforcement().GetEffectivePlanAsync(
            orgId, CancellationToken.None);
        p.Name.ShouldBe("team");
        p.Limits.MaxConcurrentRuns.ShouldBe(9);
    }

    [Fact]
    public async Task EffectivePlan_Ended_Subscription_Falls_Back_To_OrgPlan()
    {
        var orgId = await SeedOrgAsync("""{"max_concurrent_runs": 4}""");
        _db.Subscriptions.Add(new SubscriptionRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            PlanId = Guid.NewGuid(),
            Status = "ended",
            StartedAt = DateTimeOffset.UtcNow.AddDays(-60),
            EndedAt = DateTimeOffset.UtcNow.AddDays(-30),
        });
        await _db.SaveChangesAsync();
        var p = await Enforcement().GetEffectivePlanAsync(
            orgId, CancellationToken.None);
        p.Limits.MaxConcurrentRuns.ShouldBe(4);
    }

    // ---------- CheckConversationCreateAsync ----------

    [Fact]
    public async Task CheckCreate_Disabled_Enforcement_Passes()
    {
        var orgId = await SeedOrgAsync("""{"max_conversations_per_day": 1}""");
        _db.Conversations.Add(NewConversation(orgId));
        await _db.SaveChangesAsync();
        (await Enforcement(enabled: false)
            .CheckConversationCreateAsync(orgId, CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task CheckCreate_Daily_Limit_Blocks_And_Reports_Used()
    {
        var orgId = await SeedOrgAsync("""{"max_conversations_per_day": 2}""");
        _db.Conversations.AddRange(
            NewConversation(orgId), NewConversation(orgId),
            // archived não conta
            NewConversation(orgId, "Archived"),
            // ontem não conta
            new ConversationRecord
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                ExecutionStatus = "Stopped",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            });
        await _db.SaveChangesAsync();
        var err = await Enforcement()
            .CheckConversationCreateAsync(orgId, CancellationToken.None);
        err.ShouldNotBeNull();
        err.LimitKey.ShouldBe("max_conversations_per_day");
        err.Limit.ShouldBe(2);
        err.Used.ShouldBe(2);
    }

    [Fact]
    public async Task CheckCreate_Under_Limit_Passes()
    {
        var orgId = await SeedOrgAsync("""{"max_conversations_per_day": 5}""");
        _db.Conversations.Add(NewConversation(orgId));
        await _db.SaveChangesAsync();
        (await Enforcement()
            .CheckConversationCreateAsync(orgId, CancellationToken.None))
            .ShouldBeNull();
    }

    // ---------- CheckRunStartAsync ----------

    [Fact]
    public async Task CheckRun_Concurrent_Limit_Blocks()
    {
        var orgId = await SeedOrgAsync("""{"max_concurrent_runs": 1}""");
        var conv = NewConversation(orgId, "Running");
        _db.Conversations.Add(conv);
        _db.Sandboxes.Add(new SandboxRecord
        {
            Id = Guid.NewGuid(),
            SpecId = Guid.NewGuid(),
            ConversationId = conv.Id,
            Status = nameof(DomainSandboxStatus.Running),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
        var err = await Enforcement()
            .CheckRunStartAsync(orgId, CancellationToken.None);
        err.ShouldNotBeNull();
        err.LimitKey.ShouldBe("max_concurrent_runs");
        err.Used.ShouldBe(1);
    }

    [Fact]
    public async Task CheckRun_Token_Cap_Blocks()
    {
        var orgId = await SeedOrgAsync("""{"max_tokens_per_month": 1000}""");
        var period = DateTimeOffset.UtcNow.ToString("yyyy-MM");
        _db.UsageRecords.Add(new UsageRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Period = period,
            TokensUsed = 1200,
            RecordedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
        var err = await Enforcement()
            .CheckRunStartAsync(orgId, CancellationToken.None);
        err.ShouldNotBeNull();
        err.LimitKey.ShouldBe("max_tokens_per_month");
        err.Used.ShouldBe(1200);
    }

    [Fact]
    public async Task CheckRun_SandboxSeconds_Cap_Blocks()
    {
        var orgId = await SeedOrgAsync(
            """{"max_sandbox_seconds_per_month": 10}""");
        var conv = NewConversation(orgId, "Running");
        _db.Conversations.Add(conv);
        _db.Sandboxes.Add(new SandboxRecord
        {
            Id = Guid.NewGuid(),
            SpecId = Guid.NewGuid(),
            ConversationId = conv.Id,
            Status = nameof(DomainSandboxStatus.Running),
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
        var err = await Enforcement()
            .CheckRunStartAsync(orgId, CancellationToken.None);
        err.ShouldNotBeNull();
        err.LimitKey.ShouldBe("max_sandbox_seconds_per_month");
    }

    [Fact]
    public async Task CheckRun_Unlimited_Plan_Passes()
    {
        var orgId = await SeedOrgAsync("{}");
        var conv = NewConversation(orgId, "Running");
        _db.Conversations.Add(conv);
        _db.Sandboxes.Add(new SandboxRecord
        {
            Id = Guid.NewGuid(),
            SpecId = Guid.NewGuid(),
            ConversationId = conv.Id,
            Status = nameof(DomainSandboxStatus.Running),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
        (await Enforcement().CheckRunStartAsync(orgId, CancellationToken.None))
            .ShouldBeNull();
    }

    // ---------- UsageMeter ----------

    [Fact]
    public async Task Meter_Creates_Then_Aggregates_Period_Row()
    {
        var orgId = await SeedOrgAsync();
        var meter = new UsageMeter(_db);
        var period = DateTimeOffset.UtcNow.ToString("yyyy-MM");

        await meter.RecordTokensAsync(orgId, 100, 0.1m, CancellationToken.None);
        await meter.RecordTokensAsync(orgId, 50, 0.05m, CancellationToken.None);

        var rows = await _db.UsageRecords
            .Where(u => u.OrgId == orgId).ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].Period.ShouldBe(period);
        rows[0].TokensUsed.ShouldBe(150);
        rows[0].CostUsd.ShouldBe(0.15m);
    }

    [Fact]
    public async Task Meter_Zero_Tokens_No_Row()
    {
        var orgId = await SeedOrgAsync();
        await new UsageMeter(_db)
            .RecordTokensAsync(orgId, 0, null, CancellationToken.None);
        (await _db.UsageRecords.CountAsync()).ShouldBe(0);
    }

    // ---------- PlanAdminService ----------

    [Fact]
    public async Task PlanCrud_Create_Update_Delete()
    {
        var svc = new PlanAdminService(_db);
        var created = await svc.CreateAsync(
            "team", new PlanLimits(MaxConcurrentRuns: 4), 29m,
            CancellationToken.None);
        created.ShouldNotBeNull();
        created.Name.ShouldBe("team");
        created.Limits.MaxConcurrentRuns.ShouldBe(4);

        (await svc.CreateAsync("team", new PlanLimits(), null,
            CancellationToken.None)).ShouldBeNull(); // nome duplicado

        var updated = await svc.UpdateAsync(
            created.Id, null,
            new PlanLimits(MaxConversationsPerDay: 7), 39m,
            CancellationToken.None);
        updated!.Limits.MaxConversationsPerDay.ShouldBe(7);
        updated.MonthlyPriceUsd.ShouldBe(39m);

        (await svc.DeleteAsync(created.Id, CancellationToken.None))
            .ShouldBeNull();
        (await svc.DeleteAsync(created.Id, CancellationToken.None))
            .ShouldBe(PlanDeleteError.NotFound);
    }

    [Fact]
    public async Task PlanDelete_In_Use_Returns_Conflict()
    {
        var orgId = await SeedOrgAsync();
        var plan = new PlanRecord
        {
            Id = Guid.NewGuid(),
            Name = "team",
            LimitsJson = "{}",
        };
        _db.Plans.Add(plan);
        _db.Subscriptions.Add(new SubscriptionRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            PlanId = plan.Id,
            Status = "active",
            StartedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
        (await new PlanAdminService(_db)
            .DeleteAsync(plan.Id, CancellationToken.None))
            .ShouldBe(PlanDeleteError.InUse);
    }

    [Fact]
    public async Task AssignOrgPlan_Ends_Previous_Subscription()
    {
        var orgId = await SeedOrgAsync();
        var svc = new PlanAdminService(_db);
        var a = (await svc.CreateAsync("a", new PlanLimits(), null,
            CancellationToken.None))!;
        var b = (await svc.CreateAsync("b", new PlanLimits(), null,
            CancellationToken.None))!;

        (await svc.AssignOrgPlanAsync(orgId, a.Id, CancellationToken.None))
            .ShouldBeTrue();
        (await svc.AssignOrgPlanAsync(orgId, b.Id, CancellationToken.None))
            .ShouldBeTrue();

        var subs = (await _db.Subscriptions
            .Where(s => s.OrgId == orgId).ToListAsync())
            .OrderBy(s => s.StartedAt).ToList();
        subs.Count.ShouldBe(2);
        subs[0].Status.ShouldBe("ended");
        subs[0].EndedAt.ShouldNotBeNull();
        subs[1].Status.ShouldBe("active");
        (await _db.Organizations.FindAsync(orgId))!.PlanId.ShouldBe(b.Id);

        // org/plan inexistente → false
        (await svc.AssignOrgPlanAsync(Guid.NewGuid(), b.Id,
            CancellationToken.None)).ShouldBeFalse();
        (await svc.AssignOrgPlanAsync(orgId, Guid.NewGuid(),
            CancellationToken.None)).ShouldBeFalse();
    }
}