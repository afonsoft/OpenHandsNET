using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>
/// Effective-org resolution (SPEC-08 RF-003). Exact upstream order:
/// explicit X-Org-Id (membership-checked) → key bound_org_id →
/// user.current_org_id.
/// </summary>
public sealed class EffectiveOrgResolver(AppDbContext db) : IEffectiveOrgResolver
{
    /// <inheritdoc />
    public async Task<EffectiveOrg?> ResolveAsync(
        AuthUser user, string? requestedOrgHeader,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(requestedOrgHeader)
            && Guid.TryParse(requestedOrgHeader, out var headerOrg)
            && await IsMemberAsync(user.UserId, headerOrg, cancellationToken))
        {
            return new EffectiveOrg(headerOrg, "header");
        }

        if (user.BoundOrgId is { } bound)
            return new EffectiveOrg(bound, "key_binding");

        if (Guid.TryParse(user.CurrentOrgId, out var current))
            return new EffectiveOrg(current, "user_default");

        // Fallback: single membership becomes the effective org.
        var memberships = await db.OrgMembers
            .Where(m => m.UserId == user.UserId)
            .Select(m => m.OrgId)
            .ToListAsync(cancellationToken);
        return memberships.Count == 1
            ? new EffectiveOrg(memberships[0], "single_membership")
            : null;
    }

    /// <inheritdoc />
    public async Task<bool> IsMemberAsync(
        Guid userId, Guid orgId, CancellationToken cancellationToken = default) =>
        await db.OrgMembers.AnyAsync(
            m => m.OrgId == orgId && m.UserId == userId, cancellationToken);
}
