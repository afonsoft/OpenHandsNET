using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Identity;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>EF-backed <see cref="IPlanAdminService"/> (SPEC-20 RF-005).</summary>
public sealed class PlanAdminService(AppDbContext db) : IPlanAdminService
{
    private const string Active = "active";
    /// <inheritdoc />
    public async Task<IReadOnlyList<PlanInfo>> ListAsync(CancellationToken ct)
    {
        var plans = await db.Plans.AsNoTracking()
            .OrderBy(p => p.Name).ToListAsync(ct);
        var inUseSubs = await db.Subscriptions.AsNoTracking()
            .Where(s => s.Status == Active)
            .Select(s => s.PlanId).Distinct().ToListAsync(ct);
        var inUseOrgs = await db.Organizations.AsNoTracking()
            .Where(o => o.PlanId != null)
            .Select(o => o.PlanId!.Value).Distinct().ToListAsync(ct);
        return plans.Select(p => new PlanInfo(
            p.Id, p.Name, PlanLimits.Parse(p.LimitsJson), p.MonthlyPriceUsd,
            inUseSubs.Contains(p.Id), inUseOrgs.Contains(p.Id))).ToList();
    }

    /// <inheritdoc />
    public async Task<PlanInfo?> CreateAsync(
        string name, PlanLimits limits, decimal? monthlyPriceUsd,
        CancellationToken ct)
    {
        name = name.Trim();
        if (name.Length == 0 || await db.Plans
                .AnyAsync(p => p.Name == name, ct))
        {
            return null;
        }

        var plan = new PlanRecord
        {
            Id = Guid.NewGuid(),
            Name = name,
            LimitsJson = limits.ToJson(),
            MonthlyPriceUsd = monthlyPriceUsd,
        };
        db.Plans.Add(plan);
        await db.SaveChangesAsync(ct);
        return new PlanInfo(plan.Id, plan.Name, limits, monthlyPriceUsd,
            false, false);
    }

    /// <inheritdoc />
    public async Task<PlanInfo?> UpdateAsync(
        Guid id, string? name, PlanLimits? limits, decimal? monthlyPriceUsd,
        CancellationToken ct)
    {
        var plan = await db.Plans.FindAsync([id], ct);
        if (plan is null)
        {
            return null;
        }

        if (name is { Length: > 0 } n && n.Trim() != plan.Name)
        {
            if (await db.Plans.AnyAsync(p => p.Name == n.Trim(), ct))
            {
                return null; // nome duplicado
            }

            plan.Name = n.Trim();
        }

        if (limits is not null)
        {
            plan.LimitsJson = limits.ToJson();
        }

        if (monthlyPriceUsd is not null)
        {
            plan.MonthlyPriceUsd = monthlyPriceUsd;
        }

        await db.SaveChangesAsync(ct);
        return new PlanInfo(plan.Id, plan.Name,
            PlanLimits.Parse(plan.LimitsJson), plan.MonthlyPriceUsd,
            false, false);
    }

    /// <inheritdoc />
    public async Task<PlanDeleteError?> DeleteAsync(Guid id, CancellationToken ct)
    {
        var plan = await db.Plans.FindAsync([id], ct);
        if (plan is null)
        {
            return PlanDeleteError.NotFound;
        }

        var inUse = await db.Subscriptions.AnyAsync(
            s => s.PlanId == id && s.Status == Active, ct)
            || await db.Organizations.AnyAsync(o => o.PlanId == id, ct);
        if (inUse)
        {
            return PlanDeleteError.InUse;
        }

        db.Plans.Remove(plan);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <inheritdoc />
    public async Task<bool> AssignOrgPlanAsync(
        Guid orgId, Guid planId, CancellationToken ct)
    {
        var org = await db.Organizations.FindAsync([orgId], ct);
        var plan = await db.Plans.FindAsync([planId], ct);
        if (org is null || plan is null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var active = await db.Subscriptions
            .Where(s => s.OrgId == orgId && s.Status == Active)
            .ToListAsync(ct);
        foreach (var s in active)
        {
            s.Status = "ended";
            s.EndedAt = now;
        }

        db.Subscriptions.Add(new SubscriptionRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            PlanId = planId,
            Status = Active,
            StartedAt = now,
        });
        org.PlanId = planId;
        await db.SaveChangesAsync(ct);
        return true;
    }
}