namespace OpenHandsNET.Domain.Identity;

/// <summary>
/// Static role→permission maps (SPEC-08 RF-004). Two role levels: org roles
/// (<see cref="RoleName"/> values owner|admin|member on org_members) and the
/// instance-level super-role (<c>superadmin</c> on users.roles). Super-role
/// permissions are explicit — they do NOT inherit org permissions.
/// </summary>
public static class RolePermissions
{
    private static readonly Permission[] Member =
    [
        Permission.ManageSecrets,
        Permission.ManageMcp,
        Permission.ManageIntegrations,
        Permission.ManageApplicationSettings,
        Permission.ManageApiKeys,
        Permission.ViewOrgSettings,
        Permission.ViewLlmSettings,
        Permission.ViewAutomations,
    ];

    private static readonly Permission[] Admin =
    [
        ..Member,
        Permission.ManageOrgSecrets,
        Permission.ManageIntegrationProviders,
        Permission.EditLlmSettings,
        Permission.ViewBilling,
        Permission.AddCredits,
        Permission.InviteUserToOrganization,
        Permission.ChangeUserRoleMember,
        Permission.ChangeUserRoleAdmin,
        Permission.EditOrgSettings,
        Permission.ManageOrgClaims,
        Permission.ManageAutomations,
        Permission.ProvisionUser,
        Permission.ViewOrgConversations,
    ];

    private static readonly Permission[] Owner =
    [
        ..Admin,
        Permission.ChangeUserRoleOwner,
        Permission.ChangeOrganizationName,
        Permission.DeleteOrganization,
    ];

    /// <summary>Instance-level super-role ("superadmin") permissions — explicit, no org inheritance.</summary>
    private static readonly Permission[] SuperAdmin =
    [
        Permission.CreateOrganization,
        Permission.ProvisionUser,
        Permission.InviteUserToOrganization,
        Permission.ManageSuperAdmins,
        Permission.ManageFeatureFlags,
        Permission.ManageOrgQuota,
        Permission.CreateSignupLink,
        Permission.ManageOauthProviders,
    ];

    /// <summary>Permissions for an org role name (owner|admin|member). Empty for unknown roles.</summary>
    public static IReadOnlySet<Permission> ForOrgRole(string? role) => role switch
    {
        RoleName.Owner => Owner.ToHashSet(),
        RoleName.Admin => Admin.ToHashSet(),
        RoleName.Member => Member.ToHashSet(),
        _ => new HashSet<Permission>(),
    };

    /// <summary>Permissions for a super-role name. Only "superadmin" is functional.</summary>
    public static IReadOnlySet<Permission> ForSuperRole(string? role) =>
        string.Equals(role, RoleName.SuperAdmin, StringComparison.Ordinal)
            ? SuperAdmin.ToHashSet()
            : new HashSet<Permission>();
}

/// <summary>Canonical role names (wire values, lowercase).</summary>
public static class RoleName
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";

    /// <summary>Instance super-role stored on <c>users.roles</c>.</summary>
    public const string SuperAdmin = "superadmin";

    public static readonly string[] OrgRoles = [Owner, Admin, Member];

    public static bool IsOrgRole(string? role) =>
        role is Owner or Admin or Member;
}