using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Sandcastle.Application.Identity;
using Sandcastle.Domain.Identity;
using Sandcastle.Infrastructure.Identity;

namespace Sandcastle.Api.Endpoints;

/// <summary>Organization + membership + invitation + quota endpoints (SPEC-08 RF-005/006/007/010).</summary>
public static class OrgEndpoints
{
    private const string PermEditOrgSettings = "perm:edit_org_settings";
    /// <summary>Map <c>/api/v1/organizations</c> endpoints.</summary>
    public static IEndpointRouteBuilder MapOrgEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/organizations");

        g.MapGet("",
            ListOrgsAsync).WithTags("Sandcastle.Api").RequireAuthorization();

        g.MapPost("",
            CreateOrgAsync).WithTags("Sandcastle.Api").RequireAuthorization("perm:create_organization");

        g.MapGet("/{org_id:guid}",
            GetOrgAsync).WithTags("Sandcastle.Api").RequireAuthorization("perm:view_org_settings");

        g.MapPatch("/{org_id:guid}",
            UpdateOrgAsync).WithTags("Sandcastle.Api").RequireAuthorization(PermEditOrgSettings);

        g.MapDelete("/{org_id:guid}",
            DeleteOrgAsync).WithTags("Sandcastle.Api").RequireAuthorization("perm:delete_organization");

        // Members
        g.MapGet("/{org_id:guid}/members",
            ListMembersAsync).WithTags("Sandcastle.Api").RequireAuthorization();

        g.MapGet("/{org_id:guid}/members/count",
            CountMembersAsync).WithTags("Sandcastle.Api").RequireAuthorization();

        g.MapPatch("/{org_id:guid}/members/{uid:guid}",
            UpdateMemberRoleAsync).WithTags("Sandcastle.Api").RequireAuthorization();

        g.MapDelete("/{org_id:guid}/members/{uid:guid}",
            RemoveMemberAsync).WithTags("Sandcastle.Api").RequireAuthorization();

        // Settings
        g.MapGet("/{org_id:guid}/settings",
            GetOrgSettingsAsync).WithTags("Sandcastle.Api").RequireAuthorization("perm:view_org_settings");

        g.MapPatch("/{org_id:guid}/settings",
            UpdateOrgSettingsAsync).WithTags("Sandcastle.Api").RequireAuthorization(PermEditOrgSettings);

        // SPEC-30 RF-004: org memory — leitura p/ membros, delete p/ admins.
        g.MapGet("/{org_id:guid}/memory",
            GetOrgMemoryAsync).WithTags("Sandcastle.Api").RequireAuthorization();

        g.MapDelete("/{org_id:guid}/memory/{key}",
            DeleteOrgMemoryAsync).WithTags("Sandcastle.Api").RequireAuthorization(PermEditOrgSettings);

        // SPEC-48 RF-003: security policy por org — GET membros admin+
        // (view_org_settings), PUT edit_org_settings + audit diff.
        g.MapGet("/{org_id:guid}/security-policy",
            GetSecurityPolicyAsync).WithTags("Sandcastle.Api").RequireAuthorization("perm:view_org_settings");

        g.MapPut("/{org_id:guid}/security-policy",
            PutSecurityPolicyAsync).WithTags("Sandcastle.Api").RequireAuthorization(PermEditOrgSettings);

        // Quota — manage_org_quota is instance-level (super-role only).
        g.MapGet("/{org_id:guid}/quota",
            GetQuotaAsync).WithTags("Sandcastle.Api").RequireAuthorization("perm:view_billing");

        g.MapPut("/{org_id:guid}/quota",
            PutQuotaAsync).WithTags("Sandcastle.Api").RequireAuthorization("perm:manage_org_quota");

        g.MapDelete("/{org_id:guid}/quota",
            DeleteQuotaAsync).WithTags("Sandcastle.Api").RequireAuthorization("perm:manage_org_quota");

        // SPEC-14 RF-005: aggregated usage — tokens/cost (usage_records),
        // live sandbox seconds and run counts.
        g.MapGet("/{org_id:guid}/usage",
            GetUsageAsync).WithTags("Sandcastle.Api")
            .RequireAuthorization("perm:view_billing");

        // SPEC-20 RF-006: plano efetivo da org (subscrição ativa → org → free).
        g.MapGet("/{org_id:guid}/plan",
            GetOrgPlanAsync).WithTags("Sandcastle.Api").RequireAuthorization("perm:view_billing");

        // Invitations (batch + token)
        g.MapPost("/{org_id:guid}/members/invite",
            InviteMemberAsync).WithTags("Sandcastle.Api").RequireAuthorization("perm:invite_user_to_organization");

        g.MapGet("/{org_id:guid}/members/invite",
            ListInvitesAsync).WithTags("Sandcastle.Api").RequireAuthorization("perm:invite_user_to_organization");

        g.MapDelete("/{org_id:guid}/members/invite/{inv_id:guid}",
            RevokeInviteAsync).WithTags("Sandcastle.Api").RequireAuthorization("perm:invite_user_to_organization");

        // Token accept — GET redirects to signup, POST creates membership.
        g.MapGet("/members/invite/accept",
            GetAcceptInviteAsync).WithTags("Sandcastle.Api").AllowAnonymous();

        g.MapPost("/members/invite/accept",
            AcceptInviteAsync).WithTags("Sandcastle.Api").RequireAuthorization();

        // Provisioning (instance admin — gated by USER_PROVISIONING_ENABLED).
        g.MapPost("/provision-user",
            ProvisionUserAsync).WithTags("Sandcastle.Api").RequireAuthorization("perm:provision_user");

        return app;
    }

    private static async Task<IResult> ListOrgsAsync(
        ICurrentUserAccessor current,
        IOrgService orgs,
        CancellationToken ct)
    {
        var user = current.User;
        return user is null
            ? Results.Unauthorized()
            : Results.Ok(await orgs.ListMineAsync(user, ct));
    }

    private static async Task<IResult> CreateOrgAsync(
        [FromBody] OrgCreateRequest req,
        ICurrentUserAccessor current,
        IOrgService orgs,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        try
        {
            return Results.Ok(
                await orgs.CreateAsync(user, req.Name, req.Slug, ct));
        }
        catch (ArgumentException e)
        {
            return Results.BadRequest(new { detail = e.Message });
        }
        catch (OrgSlugConflictException)
        {
            return Results.Conflict(new { detail = "slug already taken" });
        }
    }

    private static async Task<IResult> GetOrgAsync(
        Guid org_id,
        ICurrentUserAccessor current,
        IOrgService orgs,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        var org = await orgs.GetAsync(user, org_id, ct);
        return org is null ? Results.NotFound() : Results.Ok(org);
    }

    private static async Task<IResult> UpdateOrgAsync(
        Guid org_id,
        [FromBody] OrgUpdateRequest req,
        ICurrentUserAccessor current,
        IOrgService orgs,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        var org = await orgs.UpdateAsync(user, org_id, req.Name, ct);
        return org is null ? Results.NotFound() : Results.Ok(org);
    }

    private static async Task<IResult> DeleteOrgAsync(
        Guid org_id,
        ICurrentUserAccessor current,
        IOrgService orgs,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        return await orgs.DeleteAsync(user, org_id, ct)
            ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> ListMembersAsync(
        Guid org_id,
        string? role,
        ICurrentUserAccessor current,
        IOrgService orgs,
        IEffectiveOrgResolver resolver,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        if (!user.IsSuperAdmin
            && !await resolver.IsMemberAsync(user.UserId, org_id, ct))
        {
            return Results.Forbid();
        }
        return Results.Ok(await orgs.ListMembersAsync(user, org_id, role, ct));
    }

    private static async Task<IResult> CountMembersAsync(
        Guid org_id,
        ICurrentUserAccessor current,
        IOrgService orgs,
        IEffectiveOrgResolver resolver,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        if (!user.IsSuperAdmin
            && !await resolver.IsMemberAsync(user.UserId, org_id, ct))
        {
            return Results.Forbid();
        }
        return Results.Ok(new { count = await orgs.CountMembersAsync(user, org_id, ct) });
    }

    private static async Task<IResult> UpdateMemberRoleAsync(
        [AsParameters] MemberRoleParams p)
    {
        var user = p.Current.User;
        if (user is null)
            return Results.Unauthorized();
        // Permissão exigida depende da role ALVO (paridade upstream).
        var required = p.Req.Role switch
        {
            RoleName.Owner => "perm:change_user_role:owner",
            RoleName.Admin => "perm:change_user_role:admin",
            RoleName.Member => "perm:change_user_role:member",
            _ => null,
        };
        if (required is null)
            return Results.BadRequest(new { detail = $"invalid role '{p.Req.Role}'" });
        var check = await p.Authz.AuthorizeAsync(p.Ctx.User, null, required);
        if (!check.Succeeded)
            return Results.Forbid();
        return await p.Orgs.ChangeMemberRoleAsync(user, p.OrgId, p.Uid, p.Req.Role, p.Ct)
            ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> RemoveMemberAsync(
        Guid org_id,
        Guid uid,
        ICurrentUserAccessor current,
        IOrgService orgs,
        IAuthorizationService authz,
        HttpContext ctx,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        var check = await authz.AuthorizeAsync(
            ctx.User, null, "perm:change_user_role:member");
        if (!check.Succeeded)
            return Results.Forbid();
        return await orgs.RemoveMemberAsync(user, org_id, uid, ct)
            ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> GetOrgSettingsAsync(
        Guid org_id,
        ICurrentUserAccessor current,
        IOrgService orgs,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        return Results.Ok(await orgs.GetSettingsAsync(user, org_id, ct));
    }

    private static async Task<IResult> UpdateOrgSettingsAsync(
        Guid org_id,
        [FromBody] Dictionary<string,
        string> settings,
        ICurrentUserAccessor current,
        IOrgService orgs,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        return await orgs.UpdateSettingsAsync(user, org_id, settings, ct)
            ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> GetOrgMemoryAsync(
        Guid org_id,
        string? query,
        ICurrentUserAccessor current,
        IEffectiveOrgResolver resolver,
        Application.Memory.IMemoryService memory,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        if (!user.IsSuperAdmin
            && !await resolver.IsMemberAsync(user.UserId, org_id, ct))
        {
            return Results.Forbid();
        }
        return Results.Ok(await memory.ListOrgAsync(org_id, query, ct));
    }

    private static async Task<IResult> DeleteOrgMemoryAsync(
        Guid org_id,
        string key,
        ICurrentUserAccessor current,
        IEffectiveOrgResolver resolver,
        Application.Memory.IMemoryService memory,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        if (!user.IsSuperAdmin
            && !await resolver.IsMemberAsync(user.UserId, org_id, ct))
        {
            return Results.Forbid();
        }
        return await memory.ForgetOrgAsync(
            org_id, key, user.UserId.ToString(), ct)
            ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> GetSecurityPolicyAsync(
        Guid org_id,
        ICurrentUserAccessor current,
        IEffectiveOrgResolver resolver,
        Sandcastle.Application.Agent.ISecurityPolicyService policies,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        if (!user.IsSuperAdmin
            && !await resolver.IsMemberAsync(user.UserId, org_id, ct))
            return Results.Forbid();
        var p = await policies.GetAsync(org_id, ct);
        return Results.Ok(new
        {
            preset = p.Preset,
            analyzer_override = p.AnalyzerOverride,
            ask_mcp_tools = p.AskMcpTools,
            min_confirm_risk = p.MinConfirmRisk.ToString()
                .ToLowerInvariant(),
            resume_strict = p.ResumeStrict,
        });
    }

    private static async Task<IResult> PutSecurityPolicyAsync(
        Guid org_id,
        [FromBody] Sandcastle.Application.Agent.SecurityPolicyUpdate? req,
        ICurrentUserAccessor current,
        Sandcastle.Application.Agent.ISecurityPolicyService policies,
        IAuditService audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        var old = await policies.GetAsync(org_id, ct);
        Sandcastle.Application.Agent.SecurityPolicy saved;
        try
        {
            saved = await policies.SetAsync(
                org_id, req, user.UserId, ct);
        }
        catch (ArgumentException e)
        {
            return Results.BadRequest(new { detail = e.Message });
        }

        await audit.RecordAsync(
            "security_policy_updated", user.UserId.ToString(), org_id,
            "security_policy", org_id.ToString(),
            ip: http.Connection.RemoteIpAddress?.ToString(),
            metadata: new { old, @new = saved },
            cancellationToken: ct);
        return Results.Ok(new
        {
            preset = saved.Preset,
            analyzer_override = saved.AnalyzerOverride,
            ask_mcp_tools = saved.AskMcpTools,
            min_confirm_risk = saved.MinConfirmRisk.ToString()
                .ToLowerInvariant(),
            resume_strict = saved.ResumeStrict,
        });
    }

    private static async Task<IResult> GetQuotaAsync(
        Guid org_id,
        ICurrentUserAccessor current,
        IQuotaService quotas,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        var quota = await quotas.GetAsync(org_id, ct);
        return quota is null
            ? Results.NotFound()
            : Results.Ok(new
            {
                daily_max = quota.DailyMax,
                concurrent_max = quota.ConcurrentMax,
            });
    }

    private static async Task<IResult> PutQuotaAsync(
        Guid org_id,
        [FromBody] QuotaRequest req,
        ICurrentUserAccessor current,
        IQuotaService quotas,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        try
        {
            await quotas.SetAsync(
                org_id, req.DailyMax, req.ConcurrentMax, ct);
        }
        catch (ArgumentException)
        {
            return Results.NotFound(new { error = "org_not_found" });
        }
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteQuotaAsync(
        Guid org_id,
        ICurrentUserAccessor current,
        IQuotaService quotas,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        return await quotas.ClearAsync(org_id, ct)
            ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> GetUsageAsync(
        Guid org_id,
        IUsageService usage,
        CancellationToken ct)
    {
        return Results.Ok(await usage.GetOrgUsageAsync(org_id, ct));
    }

    private static async Task<IResult> GetOrgPlanAsync(
        Guid org_id,
        IPlanEnforcementService plans,
        CancellationToken ct)
    {
        var plan = await plans.GetEffectivePlanAsync(org_id, ct);
        return Results.Ok(new
        {
            plan_id = plan.PlanId,
            name = plan.Name,
            limits = plan.Limits,
        });
    }

    private static async Task<IResult> InviteMemberAsync(
        Guid org_id,
        [FromBody] InviteRequest req,
        ICurrentUserAccessor current,
        IInvitationService invites,
        IAuthorizationService authz,
        HttpContext ctx,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        // Admin cannot invite as owner: role target decides the required perm.
        var required = req.Role switch
        {
            RoleName.Owner => "perm:change_user_role:owner",
            RoleName.Admin => "perm:change_user_role:admin",
            _ => "perm:change_user_role:member",
        };
        if (!(await authz.AuthorizeAsync(ctx.User, null, required)).Succeeded)
            return Results.Forbid();
        return Results.Ok(
            await invites.InviteAsync(user, org_id, req.Emails, req.Role, ct));
    }

    private static async Task<IResult> ListInvitesAsync(
        Guid org_id,
        ICurrentUserAccessor current,
        IInvitationService invites,
        CancellationToken ct)
    {
        var user = current.User;
        return user is null
            ? Results.Unauthorized()
            : Results.Ok(await invites.ListPendingAsync(user, org_id, ct));
    }

    private static async Task<IResult> RevokeInviteAsync(
        Guid org_id,
        Guid inv_id,
        ICurrentUserAccessor current,
        IInvitationService invites,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        return await invites.RevokeAsync(user, org_id, inv_id, ct)
            ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> GetAcceptInviteAsync(
        string? token,
        IInvitationService invites,
        CancellationToken ct)
    {
        var invite = token is null ? null : await invites.PeekAsync(token, ct);
        return invite is null
            ? Results.NotFound()
            : Results.Redirect($"/?invite={invite.Token}");
    }

    private static async Task<IResult> AcceptInviteAsync(
        [FromBody] AcceptInviteRequest req,
        ICurrentUserAccessor current,
        IInvitationService invites,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        return await invites.AcceptAsync(user, req.Token, ct)
            ? Results.Ok(new { accepted = true })
            : Results.BadRequest(new { detail = "invalid or expired token" });
    }

    private static async Task<IResult> ProvisionUserAsync(
        [FromBody] ProvisionRequest req,
        ICurrentUserAccessor current,
        IProvisioningService provisioning,
        IConfiguration config,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        if (!config.GetValue("Auth:UserProvisioningEnabled", false))
            return Results.NotFound();
        try
        {
            return Results.Ok(await provisioning.ProvisionAsync(
                user, req.Email, req.Name, req.OrgId, req.Role,
                req.GeneratePassword, ct));
        }
        catch (ArgumentException e)
        {
            return Results.BadRequest(new { detail = e.Message });
        }
    }

    /// <summary>Org create body.</summary>
    public sealed record OrgCreateRequest(string Name, string? Slug);

    /// <summary>Org update body.</summary>
    public sealed record OrgUpdateRequest(string? Name);

    /// <summary>Member role change body.</summary>
    public sealed record MemberRoleRequest(string Role);

    /// <summary>Binding do PATCH member role (SPEC-104 — AsParameters).</summary>
    private sealed class MemberRoleParams
    {
        [FromRoute(Name = "org_id")] public Guid OrgId { get; init; }
        [FromRoute] public Guid Uid { get; init; }
        [FromBody] public MemberRoleRequest Req { get; init; } = null!;
        [FromServices] public ICurrentUserAccessor Current { get; init; } = null!;
        [FromServices] public IOrgService Orgs { get; init; } = null!;
        [FromServices] public IAuthorizationService Authz { get; init; } = null!;
        public HttpContext Ctx { get; init; } = null!;
        public CancellationToken Ct { get; init; }
    }

    /// <summary>Quota set body.</summary>
    public sealed record QuotaRequest(int? DailyMax, int? ConcurrentMax);

    /// <summary>Batch invite body.</summary>
    public sealed record InviteRequest(IReadOnlyList<string> Emails, string Role);

    /// <summary>Accept invite body.</summary>
    public sealed record AcceptInviteRequest(string Token);

    /// <summary>Provision-user body.</summary>
    public sealed record ProvisionRequest(
        string Email, string? Name, Guid OrgId, string Role, bool GeneratePassword);
}