namespace OpenHandsNET.Application.Identity;

/// <summary>Instance-level super-admin management (SPEC-08 RF-008).</summary>
public interface ISuperAdminService
{
    Task<IReadOnlyList<SuperAdminInfo>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<bool> IsSuperAdminAsync(
        Guid userId, CancellationToken cancellationToken = default);

    Task<SuperAdminInfo?> GrantByUserIdAsync(
        Guid userId, CancellationToken cancellationToken = default);

    Task<SuperAdminInfo?> GrantByEmailAsync(
        string email, CancellationToken cancellationToken = default);

    /// <summary>Revoke super-admin. Returns false when user isn't one;
    /// throws <see cref="LastSuperAdminException"/> for the invariant.</summary>
    Task<bool> RevokeAsync(
        Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>409 — the last super admin can never be removed (SPEC-08 invariant).</summary>
public sealed class LastSuperAdminException() : InvalidOperationException(
    "cannot remove the last super admin");

/// <summary>Admin user listing/deletion.</summary>
public interface IAdminUserService
{
    Task<IReadOnlyList<SuperAdminInfo>> ListUsersAsync(
        string? query, int page, int pageSize,
        CancellationToken cancellationToken = default);

    Task<int> CountUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>Delete a user; <see cref="LastSuperAdminException"/> on last admin.</summary>
    Task<bool> DeleteUserAsync(
        Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>Admin user provisioning with compensating rollback (SPEC-08 RF-007).</summary>
public interface IProvisioningService
{
    /// <summary>
    /// Create broker user (email-verified, skips TOS/onboarding) + local records
    /// + membership + initial API key. Rollback deletes broker user and local
    /// rows on failure.
    /// </summary>
    Task<ProvisionedUser> ProvisionAsync(
        AuthUser actor, string email, string? name, Guid orgId, string role,
        bool generatePassword, CancellationToken cancellationToken = default);
}

/// <summary>Device authorization flow (RFC 8628) — issues 7-day sk-* keys.</summary>
public interface IDeviceFlowService
{
    Task<DeviceAuthorization> StartAsync(
        string baseUrl, CancellationToken cancellationToken = default);

    /// <summary>Poll for a token; returns null while pending.</summary>
    Task<DeviceTokenResult> PollAsync(
        string deviceCode, CancellationToken cancellationToken = default);

    /// <summary>Approve or deny a user_code from an authenticated session.</summary>
    Task<bool> VerifyAsync(
        AuthUser user, string userCode, bool approve,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of polling the device token endpoint.</summary>
public sealed record DeviceTokenResult(DeviceTokenStatus Status, string? AccessToken = null);

public enum DeviceTokenStatus
{
    /// <summary>Keep polling.</summary>
    Pending,

    /// <summary>Approved — access_token carries a 7-day sk-* key.</summary>
    Approved,

    /// <summary>User denied the request.</summary>
    Denied,

    /// <summary>Code expired.</summary>
    Expired,

    /// <summary>Unknown device_code.</summary>
    Invalid,
}

/// <summary>Org conversation quota (SPEC-08 RF-010) — manage_org_quota is instance-level.</summary>
public interface IQuotaService
{
    Task<OrgQuota?> GetAsync(Guid orgId, CancellationToken cancellationToken = default);

    Task SetAsync(
        Guid orgId, int? dailyMax, int? concurrentMax,
        CancellationToken cancellationToken = default);

    Task<bool> ClearAsync(Guid orgId, CancellationToken cancellationToken = default);
}

/// <summary>Append-only audit trail (SPEC-08 RF-011 — OpenHandsNET addition).</summary>
public interface IAuditService
{
    Task RecordAsync(
        string action, string? actorUserId = null, Guid? orgId = null,
        string? targetType = null, string? targetId = null,
        string? ip = null, string? userAgent = null,
        object? metadata = null, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<AuditEntry> Items, int Total)> QueryAsync(
        string? userId, string? action, DateTimeOffset? from, DateTimeOffset? to,
        int page, int pageSize, CancellationToken cancellationToken = default);
}

/// <summary>External analytics (none|posthog — parity, optional).</summary>
public interface IAnalyticsService
{
    Task TrackAsync(
        string eventName, string? distinctId, object? properties,
        CancellationToken cancellationToken = default);
}

/// <summary>Instance feature flags.</summary>
public interface IFeatureFlagService
{
    Task<IReadOnlyList<FeatureFlagInfo>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<bool> GetAsync(string name, CancellationToken cancellationToken = default);

    Task SetAsync(
        string name, bool enabled, string? description,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);
}
