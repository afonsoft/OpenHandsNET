using OpenHandsNET.Domain.Identity;

namespace OpenHandsNET.Application.Identity;

/// <summary>Plan catalog row for admin APIs (SPEC-20 RF-005).</summary>
public sealed record PlanInfo(
    Guid Id, string Name, PlanLimits Limits, decimal? MonthlyPriceUsd,
    bool HasActiveSubscriptions, bool HasOrgs);

/// <summary>Delete rejection reasons.</summary>
public enum PlanDeleteError
{
    /// <summary>404 — plan id not found.</summary>
    NotFound,

    /// <summary>409 — active subscription or org still references the plan.</summary>
    InUse,
}

/// <summary>
/// Admin CRUD over the plan catalog + org plan assignment
/// (SPEC-20 RF-005). Assignment creates a new active subscription and ends
/// the previous one — no payment gateway involved
/// (<c>IBillingProvider</c> stays the documented extension seam).
/// </summary>
public interface IPlanAdminService
{
    /// <summary>Lists the plan catalog with usage flags.</summary>
    Task<IReadOnlyList<PlanInfo>> ListAsync(CancellationToken ct);

    /// <summary>Creates a plan. Returns null when the name already exists.</summary>
    Task<PlanInfo?> CreateAsync(
        string name, PlanLimits limits, decimal? monthlyPriceUsd,
        CancellationToken ct);

    /// <summary>Updates limits/price/name; null when not found.</summary>
    Task<PlanInfo?> UpdateAsync(
        Guid id, string? name, PlanLimits? limits, decimal? monthlyPriceUsd,
        CancellationToken ct);

    /// <summary>Deletes a plan; <see cref="PlanDeleteError.InUse"/> when referenced.</summary>
    Task<PlanDeleteError?> DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Assigns a plan to the org: ends the active subscription, starts a new
    /// one and updates <c>OrganizationRecord.PlanId</c>. Returns false when
    /// org or plan does not exist.
    /// </summary>
    Task<bool> AssignOrgPlanAsync(Guid orgId, Guid planId, CancellationToken ct);
}
