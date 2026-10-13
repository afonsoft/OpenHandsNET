using OpenHandsNET.Domain.Identity;
using Shouldly;

namespace OpenHandsNET.UnitTests.Identity;

/// <summary>Wire-name contract — snake_case + colon separator (SPEC-08 RF-004).</summary>
public sealed class PermissionWireTests
{
    [Fact]
    public void All_Permissions_RoundTrip_Wire_Names()
    {
        foreach (var permission in Enum.GetValues<Permission>())
        {
            var wire = permission.ToWireName();
            wire.ShouldNotBeNullOrWhiteSpace();
            PermissionExtensions.Parse(wire).ShouldBe(permission);
        }
    }

    [Fact]
    public void Wire_Names_Match_Upstream_Contract()
    {
        Permission.ChangeUserRoleOwner.ToWireName().ShouldBe("change_user_role:owner");
        Permission.ManageApiKeys.ToWireName().ShouldBe("manage_api_keys");
        Permission.ViewOrgConversations.ToWireName()
            .ShouldBe("view_org_conversations");
        Permission.ManageOauthProviders.ToWireName()
            .ShouldBe("manage_oauth_providers");
    }

    [Fact]
    public void Parse_Unknown_Returns_Null()
    {
        PermissionExtensions.Parse("not_a_perm").ShouldBeNull();
        PermissionExtensions.Parse(null).ShouldBeNull();
        PermissionExtensions.Parse("").ShouldBeNull();
    }

    [Fact]
    public void Wire_Names_Are_Unique()
    {
        var names = Enum.GetValues<Permission>().Select(p => p.ToWireName()).ToList();
        names.ShouldBeUnique();
    }
}
