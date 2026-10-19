namespace OpenHandsNET.Application.Identity;

/// <summary>
/// Resolves the effective org for a request (SPEC-08 RF-003). Order (exact
/// upstream parity): explicit X-Org-Id header (membership-checked) →
/// bound_org_id of the API key → user.current_org_id.
/// </summary>
public interface IEffectiveOrgResolver
{
    /// <summary>Resolve effective org for the user; null when none applies.</summary>
    Task<EffectiveOrg?> ResolveAsync(
        AuthUser user,
        string? requestedOrgHeader,
        CancellationToken cancellationToken = default);

    /// <summary>True when the user is a member of the org.</summary>
    Task<bool> IsMemberAsync(
        Guid userId, Guid orgId, CancellationToken cancellationToken = default);
}