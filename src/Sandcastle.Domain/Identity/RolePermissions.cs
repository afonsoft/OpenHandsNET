namespace Sandcastle.Domain.Identity;

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
        Permission.WikiRead,
        // SPEC-143: member dispara scans e triagia findings.
        Permission.ScansRun,
        Permission.ScansFindings,
        // SPEC-144: member cria/executa refactors — aprovação fica admin+.
        Permission.RefactorsRun,
        // SPEC-148: member instala entries no próprio user scope.
        Permission.MarketplaceInstall,
        // SPEC-151: member lê fontes/eventos de alerta da org.
        Permission.AlertsView,
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
        Permission.IndexingManage,
        // SPEC-143: admin gerencia schedules/lifecycle de scans.
        Permission.ScansManage,
        // SPEC-144 §9: aprovação de plano de refactor exige admin+
        // (plano é untrusted até revisão humana).
        Permission.RefactorsApprove,
        // SPEC-148: admin+ instala/gerencia entries no scope da org.
        Permission.MarketplaceManage,
        // SPEC-151: admin+ gerencia fontes e ações de alerta.
        Permission.AlertsManage,
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
        // SPEC-148: system scope = bundle da plataforma (só superadmin);
        // install é incluído pois o super-role não herda org perms.
        Permission.MarketplaceInstall,
        Permission.MarketplaceManage,
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