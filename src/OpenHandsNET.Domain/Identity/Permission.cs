namespace OpenHandsNET.Domain.Identity;

/// <summary>
/// Permission enum — SPEC-08 RF-004. Snake_case wire names via
/// <see cref="PermissionExtensions.ToWireName"/>: the wire contract keeps the
/// upstream names (e.g. "change_user_role:owner") for compatibility.
/// </summary>
public enum Permission
{
    ManageSecrets,
    ManageOrgSecrets,
    ManageMcp,
    ManageIntegrations,
    ManageIntegrationProviders,
    ManageApplicationSettings,
    ManageApiKeys,
    ViewLlmSettings,
    EditLlmSettings,
    ViewBilling,
    AddCredits,
    InviteUserToOrganization,
    ChangeUserRoleMember,
    ChangeUserRoleAdmin,
    ChangeUserRoleOwner,
    ViewOrgSettings,
    ChangeOrganizationName,
    DeleteOrganization,
    CreateOrganization,
    EditOrgSettings,
    ManageOrgClaims,
    ViewAutomations,
    ManageAutomations,
    ProvisionUser,
    ViewOrgConversations,
    ManageSuperAdmins,
    ManageFeatureFlags,
    ManageOrgQuota,
    CreateSignupLink,
    ManageOauthProviders,
}

public static class PermissionExtensions
{
    /// <summary>Wire name used by the API/audit (e.g. <c>change_user_role:owner</c>).</summary>
    public static string ToWireName(this Permission permission) => permission switch
    {
        Permission.ManageSecrets => "manage_secrets",
        Permission.ManageOrgSecrets => "manage_org_secrets",
        Permission.ManageMcp => "manage_mcp",
        Permission.ManageIntegrations => "manage_integrations",
        Permission.ManageIntegrationProviders => "manage_integration_providers",
        Permission.ManageApplicationSettings => "manage_application_settings",
        Permission.ManageApiKeys => "manage_api_keys",
        Permission.ViewLlmSettings => "view_llm_settings",
        Permission.EditLlmSettings => "edit_llm_settings",
        Permission.ViewBilling => "view_billing",
        Permission.AddCredits => "add_credits",
        Permission.InviteUserToOrganization => "invite_user_to_organization",
        Permission.ChangeUserRoleMember => "change_user_role:member",
        Permission.ChangeUserRoleAdmin => "change_user_role:admin",
        Permission.ChangeUserRoleOwner => "change_user_role:owner",
        Permission.ViewOrgSettings => "view_org_settings",
        Permission.ChangeOrganizationName => "change_organization_name",
        Permission.DeleteOrganization => "delete_organization",
        Permission.CreateOrganization => "create_organization",
        Permission.EditOrgSettings => "edit_org_settings",
        Permission.ManageOrgClaims => "manage_org_claims",
        Permission.ViewAutomations => "view_automations",
        Permission.ManageAutomations => "manage_automations",
        Permission.ProvisionUser => "provision_user",
        Permission.ViewOrgConversations => "view_org_conversations",
        Permission.ManageSuperAdmins => "manage_super_admins",
        Permission.ManageFeatureFlags => "manage_feature_flags",
        Permission.ManageOrgQuota => "manage_org_quota",
        Permission.CreateSignupLink => "create_signup_link",
        Permission.ManageOauthProviders => "manage_oauth_providers",
        _ => throw new ArgumentOutOfRangeException(nameof(permission)),
    };

    /// <summary>Parses a wire name back into the enum (null when unknown).</summary>
    public static Permission? Parse(string? wireName) => wireName switch
    {
        "manage_secrets" => Permission.ManageSecrets,
        "manage_org_secrets" => Permission.ManageOrgSecrets,
        "manage_mcp" => Permission.ManageMcp,
        "manage_integrations" => Permission.ManageIntegrations,
        "manage_integration_providers" => Permission.ManageIntegrationProviders,
        "manage_application_settings" => Permission.ManageApplicationSettings,
        "manage_api_keys" => Permission.ManageApiKeys,
        "view_llm_settings" => Permission.ViewLlmSettings,
        "edit_llm_settings" => Permission.EditLlmSettings,
        "view_billing" => Permission.ViewBilling,
        "add_credits" => Permission.AddCredits,
        "invite_user_to_organization" => Permission.InviteUserToOrganization,
        "change_user_role:member" => Permission.ChangeUserRoleMember,
        "change_user_role:admin" => Permission.ChangeUserRoleAdmin,
        "change_user_role:owner" => Permission.ChangeUserRoleOwner,
        "view_org_settings" => Permission.ViewOrgSettings,
        "change_organization_name" => Permission.ChangeOrganizationName,
        "delete_organization" => Permission.DeleteOrganization,
        "create_organization" => Permission.CreateOrganization,
        "edit_org_settings" => Permission.EditOrgSettings,
        "manage_org_claims" => Permission.ManageOrgClaims,
        "view_automations" => Permission.ViewAutomations,
        "manage_automations" => Permission.ManageAutomations,
        "provision_user" => Permission.ProvisionUser,
        "view_org_conversations" => Permission.ViewOrgConversations,
        "manage_super_admins" => Permission.ManageSuperAdmins,
        "manage_feature_flags" => Permission.ManageFeatureFlags,
        "manage_org_quota" => Permission.ManageOrgQuota,
        "create_signup_link" => Permission.CreateSignupLink,
        "manage_oauth_providers" => Permission.ManageOauthProviders,
        _ => null,
    };
}
