using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Identity;
using OpenHandsNET.Infrastructure.Identity;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>Organization + membership + invitation + quota endpoints (SPEC-08 RF-005/006/007/010).</summary>
public static class OrgEndpoints
{
    /// <summary>Map <c>/api/v1/organizations</c> endpoints.</summary>
    public static IEndpointRouteBuilder MapOrgEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/organizations");

        g.MapGet("", async (
            ICurrentUserAccessor current, IOrgService orgs, CancellationToken ct) =>
        {
            var user = current.User;
            return user is null
                ? Results.Unauthorized()
                : Results.Ok(await orgs.ListMineAsync(user, ct));
        }).RequireAuthorization();

        g.MapPost("", async (
            [FromBody] OrgCreateRequest req,
            ICurrentUserAccessor current, IOrgService orgs, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            return Results.Ok(
                await orgs.CreateAsync(user, req.Name, req.Slug, ct));
        }).RequireAuthorization("perm:create_organization");

        g.MapGet("/{org_id:guid}", async (
            Guid org_id, ICurrentUserAccessor current, IOrgService orgs,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            var org = await orgs.GetAsync(user, org_id, ct);
            return org is null ? Results.NotFound() : Results.Ok(org);
        }).RequireAuthorization("perm:view_org_settings");

        g.MapPatch("/{org_id:guid}", async (
            Guid org_id, [FromBody] OrgUpdateRequest req,
            ICurrentUserAccessor current, IOrgService orgs, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            var org = await orgs.UpdateAsync(user, org_id, req.Name, ct);
            return org is null ? Results.NotFound() : Results.Ok(org);
        }).RequireAuthorization("perm:edit_org_settings");

        g.MapDelete("/{org_id:guid}", async (
            Guid org_id, ICurrentUserAccessor current, IOrgService orgs,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            return await orgs.DeleteAsync(user, org_id, ct)
                ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization("perm:delete_organization");

        // Members
        g.MapGet("/{org_id:guid}/members", async (
            Guid org_id, string? role,
            ICurrentUserAccessor current, IOrgService orgs,
            IEffectiveOrgResolver resolver, CancellationToken ct) =>
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
        }).RequireAuthorization();

        g.MapGet("/{org_id:guid}/members/count", async (
            Guid org_id, ICurrentUserAccessor current, IOrgService orgs,
            IEffectiveOrgResolver resolver, CancellationToken ct) =>
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
        }).RequireAuthorization();

        g.MapPatch("/{org_id:guid}/members/{uid:guid}", async (
            Guid org_id, Guid uid, [FromBody] MemberRoleRequest req,
            ICurrentUserAccessor current, IOrgService orgs,
            IAuthorizationService authz, HttpContext ctx, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            // Permissão exigida depende da role ALVO (paridade upstream).
            var required = req.Role switch
            {
                RoleName.Owner => "perm:change_user_role:owner",
                RoleName.Admin => "perm:change_user_role:admin",
                RoleName.Member => "perm:change_user_role:member",
                _ => null,
            };
            if (required is null)
                return Results.BadRequest(new { detail = $"invalid role '{req.Role}'" });
            var check = await authz.AuthorizeAsync(ctx.User, null, required);
            if (!check.Succeeded)
                return Results.Forbid();
            return await orgs.ChangeMemberRoleAsync(user, org_id, uid, req.Role, ct)
                ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization();

        g.MapDelete("/{org_id:guid}/members/{uid:guid}", async (
            Guid org_id, Guid uid,
            ICurrentUserAccessor current, IOrgService orgs,
            IAuthorizationService authz, HttpContext ctx, CancellationToken ct) =>
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
        }).RequireAuthorization();

        // Settings
        g.MapGet("/{org_id:guid}/settings", async (
            Guid org_id, ICurrentUserAccessor current, IOrgService orgs,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            return Results.Ok(await orgs.GetSettingsAsync(user, org_id, ct));
        }).RequireAuthorization("perm:view_org_settings");

        g.MapPatch("/{org_id:guid}/settings", async (
            Guid org_id, [FromBody] Dictionary<string, string> settings,
            ICurrentUserAccessor current, IOrgService orgs, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            return await orgs.UpdateSettingsAsync(user, org_id, settings, ct)
                ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization("perm:edit_org_settings");

        // SPEC-30 RF-004: org memory — leitura p/ membros, delete p/ admins.
        g.MapGet("/{org_id:guid}/memory", async (
            Guid org_id, string? query,
            ICurrentUserAccessor current, IEffectiveOrgResolver resolver,
            Application.Memory.IMemoryService memory, CancellationToken ct) =>
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
        }).RequireAuthorization();

        g.MapDelete("/{org_id:guid}/memory/{key}", async (
            Guid org_id, string key,
            ICurrentUserAccessor current, IEffectiveOrgResolver resolver,
            Application.Memory.IMemoryService memory, CancellationToken ct) =>
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
        }).RequireAuthorization("perm:edit_org_settings");

        // SPEC-48 RF-003: security policy por org — GET membros admin+
        // (view_org_settings), PUT edit_org_settings + audit diff.
        g.MapGet("/{org_id:guid}/security-policy", async (
            Guid org_id, ICurrentUserAccessor current,
            IEffectiveOrgResolver resolver,
            OpenHandsNET.Application.Agent.ISecurityPolicyService policies,
            CancellationToken ct) =>
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
        }).RequireAuthorization("perm:view_org_settings");

        g.MapPut("/{org_id:guid}/security-policy", async (
            Guid org_id,
            [FromBody] OpenHandsNET.Application.Agent.SecurityPolicyUpdate? req,
            ICurrentUserAccessor current,
            OpenHandsNET.Application.Agent.ISecurityPolicyService policies,
            IAuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            var old = await policies.GetAsync(org_id, ct);
            OpenHandsNET.Application.Agent.SecurityPolicy saved;
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
        }).RequireAuthorization("perm:edit_org_settings");

        // Quota — manage_org_quota is instance-level (super-role only).
        g.MapGet("/{org_id:guid}/quota", async (
            Guid org_id, ICurrentUserAccessor current, IQuotaService quotas,
            CancellationToken ct) =>
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
        }).RequireAuthorization("perm:view_billing");

        g.MapPut("/{org_id:guid}/quota", async (
            Guid org_id, [FromBody] QuotaRequest req,
            ICurrentUserAccessor current, IQuotaService quotas, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            await quotas.SetAsync(org_id, req.DailyMax, req.ConcurrentMax, ct);
            return Results.NoContent();
        }).RequireAuthorization("perm:manage_org_quota");

        g.MapDelete("/{org_id:guid}/quota", async (
            Guid org_id, ICurrentUserAccessor current, IQuotaService quotas,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            return await quotas.ClearAsync(org_id, ct)
                ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization("perm:manage_org_quota");

        // SPEC-14 RF-005: aggregated usage — tokens/cost (usage_records),
        // live sandbox seconds and run counts.
        g.MapGet("/{org_id:guid}/usage", async (
            Guid org_id, IUsageService usage, CancellationToken ct) =>
            Results.Ok(await usage.GetOrgUsageAsync(org_id, ct)))
            .RequireAuthorization("perm:view_billing");

        // SPEC-20 RF-006: plano efetivo da org (subscrição ativa → org → free).
        g.MapGet("/{org_id:guid}/plan", async (
            Guid org_id, IPlanEnforcementService plans, CancellationToken ct) =>
        {
            var plan = await plans.GetEffectivePlanAsync(org_id, ct);
            return Results.Ok(new
            {
                plan_id = plan.PlanId,
                name = plan.Name,
                limits = plan.Limits,
            });
        }).RequireAuthorization("perm:view_billing");

        // Invitations (batch + token)
        g.MapPost("/{org_id:guid}/members/invite", async (
            Guid org_id, [FromBody] InviteRequest req,
            ICurrentUserAccessor current, IInvitationService invites,
            IAuthorizationService authz, HttpContext ctx, CancellationToken ct) =>
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
        }).RequireAuthorization("perm:invite_user_to_organization");

        g.MapGet("/{org_id:guid}/members/invite", async (
            Guid org_id, ICurrentUserAccessor current, IInvitationService invites,
            CancellationToken ct) =>
        {
            var user = current.User;
            return user is null
                ? Results.Unauthorized()
                : Results.Ok(await invites.ListPendingAsync(user, org_id, ct));
        }).RequireAuthorization("perm:invite_user_to_organization");

        g.MapDelete("/{org_id:guid}/members/invite/{inv_id:guid}", async (
            Guid org_id, Guid inv_id,
            ICurrentUserAccessor current, IInvitationService invites,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            return await invites.RevokeAsync(user, org_id, inv_id, ct)
                ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization("perm:invite_user_to_organization");

        // Token accept — GET redirects to signup, POST creates membership.
        g.MapGet("/members/invite/accept", async (
            string? token, IInvitationService invites, CancellationToken ct) =>
        {
            var invite = token is null ? null : await invites.PeekAsync(token, ct);
            return invite is null
                ? Results.NotFound()
                : Results.Redirect($"/?invite={invite.Token}");
        }).AllowAnonymous();

        g.MapPost("/members/invite/accept", async (
            [FromBody] AcceptInviteRequest req,
            ICurrentUserAccessor current, IInvitationService invites,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            return await invites.AcceptAsync(user, req.Token, ct)
                ? Results.Ok(new { accepted = true })
                : Results.BadRequest(new { detail = "invalid or expired token" });
        }).RequireAuthorization();

        // Provisioning (instance admin — gated by USER_PROVISIONING_ENABLED).
        g.MapPost("/provision-user", async (
            [FromBody] ProvisionRequest req,
            ICurrentUserAccessor current, IProvisioningService provisioning,
            IConfiguration config, CancellationToken ct) =>
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
        }).RequireAuthorization("perm:provision_user");

        return app;
    }

    /// <summary>Org create body.</summary>
    public sealed record OrgCreateRequest(string Name, string? Slug);

    /// <summary>Org update body.</summary>
    public sealed record OrgUpdateRequest(string? Name);

    /// <summary>Member role change body.</summary>
    public sealed record MemberRoleRequest(string Role);

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