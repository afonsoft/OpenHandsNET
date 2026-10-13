namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>Auth configuration (SPEC-08 RF-001/RF-002/RF-009/RF-013).</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary><c>none</c> (OSS local dev) or <c>oidc</c> (broker: Keycloak etc.).</summary>
    public string Provider { get; set; } = "none";

    /// <summary>OIDC authority (Keycloak realm URL etc.).</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>Optional JWT bearer scheme for compat.</summary>
    public bool EnableJwt { get; set; }

    /// <summary>Device-flow issued API key lifetime in days (RF-009).</summary>
    public int DeviceKeyExpirationDays { get; set; } = 7;

    /// <summary>USER_PROVISIONING_ENABLED feature flag (RF-007/RF-008).</summary>
    public bool UserProvisioningEnabled { get; set; }

    /// <summary>ENABLE_SUPER_ADMIN flag — super-admin API even in oss.</summary>
    public bool EnableSuperAdmin { get; set; }
}
