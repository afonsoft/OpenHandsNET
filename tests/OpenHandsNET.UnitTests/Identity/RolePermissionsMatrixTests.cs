using OpenHandsNET.Domain.Identity;

using Shouldly;

namespace OpenHandsNET.UnitTests.Identity;

/// <summary>SPEC-08 RF-004: ROLE_PERMISSIONS/SUPER_ROLE_PERMISSIONS matrix.</summary>
public sealed class RolePermissionsMatrixTests
{
    [Fact]
    public void Member_Has_Base_Permissions()
    {
        var perms = RolePermissions.ForOrgRole(RoleName.Member);
        perms.ShouldContain(Permission.ManageSecrets);
        perms.ShouldContain(Permission.ManageMcp);
        perms.ShouldContain(Permission.ManageApiKeys);
        perms.ShouldContain(Permission.ViewOrgSettings);
        perms.ShouldContain(Permission.ViewLlmSettings);
        perms.ShouldNotContain(Permission.EditOrgSettings);
        perms.ShouldNotContain(Permission.DeleteOrganization);
        perms.ShouldNotContain(Permission.InviteUserToOrganization);
    }

    [Fact]
    public void Admin_Contains_Member_Plus_Management()
    {
        var member = RolePermissions.ForOrgRole(RoleName.Member);
        var admin = RolePermissions.ForOrgRole(RoleName.Admin);
        member.ShouldBeSubsetOf(admin);
        admin.ShouldContain(Permission.ManageOrgSecrets);
        admin.ShouldContain(Permission.InviteUserToOrganization);
        admin.ShouldContain(Permission.ChangeUserRoleMember);
        admin.ShouldContain(Permission.ChangeUserRoleAdmin);
        admin.ShouldContain(Permission.EditOrgSettings);
        admin.ShouldContain(Permission.ViewBilling);
        admin.ShouldContain(Permission.ProvisionUser);
        // Admin cannot grant owner.
        admin.ShouldNotContain(Permission.ChangeUserRoleOwner);
        admin.ShouldNotContain(Permission.DeleteOrganization);
    }

    [Fact]
    public void Owner_Contains_Admin_Plus_Ownership()
    {
        var admin = RolePermissions.ForOrgRole(RoleName.Admin);
        var owner = RolePermissions.ForOrgRole(RoleName.Owner);
        admin.ShouldBeSubsetOf(owner);
        owner.ShouldContain(Permission.ChangeUserRoleOwner);
        owner.ShouldContain(Permission.ChangeOrganizationName);
        owner.ShouldContain(Permission.DeleteOrganization);
    }

    [Fact]
    public void SuperAdmin_Has_Explicit_Instance_Perms_No_Org_Inheritance()
    {
        var super = RolePermissions.ForSuperRole(RoleName.SuperAdmin);
        super.ShouldContain(Permission.CreateOrganization);
        super.ShouldContain(Permission.ManageSuperAdmins);
        super.ShouldContain(Permission.ManageFeatureFlags);
        super.ShouldContain(Permission.ManageOrgQuota);
        super.ShouldContain(Permission.ProvisionUser);
        super.ShouldContain(Permission.InviteUserToOrganization);
        // No org-role inheritance — these are org perms, not instance perms.
        super.ShouldNotContain(Permission.DeleteOrganization);
        super.ShouldNotContain(Permission.EditOrgSettings);
        super.ShouldNotContain(Permission.ViewBilling);
    }

    [Fact]
    public void Unknown_Roles_Have_No_Permissions()
    {
        RolePermissions.ForOrgRole(null).ShouldBeEmpty();
        RolePermissions.ForOrgRole("bogus").ShouldBeEmpty();
        RolePermissions.ForSuperRole("admin").ShouldBeEmpty();
        RolePermissions.ForSuperRole(null).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("owner"), InlineData("admin"), InlineData("member")]
    public void RoleName_IsOrgRole(string role) =>
        RoleName.IsOrgRole(role).ShouldBeTrue();

    [Theory]
    [InlineData("superadmin"), InlineData("bogus"), InlineData(null)]
    public void RoleName_Rejects_NonOrgRoles(string? role) =>
        RoleName.IsOrgRole(role).ShouldBeFalse();
}