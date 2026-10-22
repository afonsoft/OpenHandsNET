using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Identity;
using OpenHandsNET.Infrastructure.Identity;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>Instance admin endpoints: super-admins, users, audit, feature flags (SPEC-08 RF-008/011).</summary>
public static class AdminEndpoints
{
    private const string PermManageSuperAdmins = "perm:manage_super_admins";
    /// <summary>Map <c>/api/v1/admin</c> endpoints.</summary>
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/admin");

        // Super admins — list open to any authenticated user when
        // USER_PROVISIONING_ENABLED (upstream OHE-3196 parity); writes are perm-gated.
        g.MapGet("/super-admins", async (
            ISuperAdminService admins, IConfiguration config,
            ICurrentUserAccessor current, HttpContext ctx,
            IAuthorizationService authz, CancellationToken ct) =>
        {
            if (current.User is null)
                return Results.Unauthorized();
            if (config.GetValue("Auth:UserProvisioningEnabled", false))
                return Results.Ok(await admins.ListAsync(ct));
            return (await authz.AuthorizeAsync(
                ctx.User, null, PermManageSuperAdmins)).Succeeded
                ? Results.Ok(await admins.ListAsync(ct))
                : Results.Forbid();
        }).RequireAuthorization();

        g.MapGet("/super-admins/me", async (
            ICurrentUserAccessor current, ISuperAdminService admins,
            CancellationToken ct) =>
        {
            var user = current.User;
            return user is null
                ? Results.Unauthorized()
                : Results.Ok(new
                {
                    is_super_admin = await admins.IsSuperAdminAsync(user.UserId, ct),
                });
        }).RequireAuthorization();

        g.MapPost("/super-admins", async (
            [FromBody] GrantRequest req,
            ICurrentUserAccessor current, ISuperAdminService admins,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            var granted = await GrantAsync(req, admins, ct);
            return granted is null ? Results.NotFound() : Results.Ok(granted);
        }).RequireAuthorization(PermManageSuperAdmins);

        g.MapDelete("/super-admins/{uid:guid}", async (
            Guid uid, ICurrentUserAccessor current, ISuperAdminService admins,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            try
            {
                return await admins.RevokeAsync(uid, ct)
                    ? Results.NoContent() : Results.NotFound();
            }
            catch (LastSuperAdminException e)
            {
                return Results.Conflict(new { detail = e.Message });
            }
        }).RequireAuthorization(PermManageSuperAdmins);

        static async Task<object?> GrantAsync(
            GrantRequest req, ISuperAdminService admins, CancellationToken ct)
        {
            if (req.UserId is { } uid)
            {
                return await admins.GrantByUserIdAsync(uid, ct);
            }

            if (req.Email is { } email)
            {
                return await admins.GrantByEmailAsync(email, ct);
            }

            return null;
        }

        // Admin users
        g.MapGet("/users", async (
            string? q, int? page,
            IAdminUserService users, CancellationToken ct) =>
            Results.Ok(await users.ListUsersAsync(
                q, page ?? 1, 50, ct)))
            .RequireAuthorization(PermManageSuperAdmins);

        g.MapGet("/users/count", async (
            IAdminUserService users, CancellationToken ct) =>
            Results.Ok(new { count = await users.CountUsersAsync(ct) }))
            .RequireAuthorization(PermManageSuperAdmins);

        g.MapDelete("/users/{uid:guid}", async (
            Guid uid, IAdminUserService users, CancellationToken ct) =>
        {
            try
            {
                return await users.DeleteUserAsync(uid, ct)
                    ? Results.NoContent() : Results.NotFound();
            }
            catch (LastSuperAdminException e)
            {
                return Results.Conflict(new { detail = e.Message });
            }
        }).RequireAuthorization(PermManageSuperAdmins);

        // Audit trail query (RF-011) + CSV/JSON export.
        g.MapGet("/audit", async (
            string? user, string? action, DateTimeOffset? from, DateTimeOffset? to,
            int? page, string? format,
            IAuditService audit, CancellationToken ct) =>
        {
            var (items, total) = await audit.QueryAsync(
                user, action, from, to, page ?? 1, 100, ct);
            if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            {
                var csv = "id,actor_user_id,org_id,action,target_type,target_id,timestamp\n"
                    + string.Join("\n", items.Select(e =>
                        $"{e.Id},{e.ActorUserId},{e.OrgId},{e.Action},{e.TargetType},{e.TargetId},{e.Timestamp:O}"));
                return Results.Text(csv, "text/csv");
            }
            return Results.Ok(new { items, total });
        }).RequireAuthorization(PermManageSuperAdmins);

        // Feature flags
        g.MapGet("/feature-flags", async (
            IFeatureFlagService flags, CancellationToken ct) =>
            Results.Ok(await flags.ListAsync(ct)))
            .RequireAuthorization("perm:manage_feature_flags");

        g.MapPut("/feature-flags/{name}", async (
            string name, [FromBody] FlagRequest req,
            IFeatureFlagService flags, CancellationToken ct) =>
        {
            await flags.SetAsync(name, req.Enabled, req.Description, ct);
            return Results.NoContent();
        }).RequireAuthorization("perm:manage_feature_flags");

        g.MapDelete("/feature-flags/{name}", async (
            string name, IFeatureFlagService flags, CancellationToken ct) =>
            await flags.DeleteAsync(name, ct)
                ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization("perm:manage_feature_flags");

        // SPEC-20 RF-005: catálogo de planos (limits em snake_case via
        // naming policy global). Delete 409 enquanto houver referência ativa.
        g.MapGet("/plans", async (
            IPlanAdminService plans, CancellationToken ct) =>
            Results.Ok(await plans.ListAsync(ct)))
            .RequireAuthorization(PermManageSuperAdmins);

        g.MapPost("/plans", async (
            [FromBody] PlanCreateRequest req, IPlanAdminService plans,
            CancellationToken ct) =>
            await plans.CreateAsync(
                req.Name, req.Limits ?? new(), req.MonthlyPriceUsd, ct)
                is { } p ? Results.Created($"/api/v1/admin/plans/{p.Id}", p)
                : Results.Conflict(new { detail = "plan name already exists" }))
            .RequireAuthorization(PermManageSuperAdmins);

        g.MapPatch("/plans/{id:guid}", async (
            Guid id, [FromBody] PlanPatchRequest req,
            IPlanAdminService plans, CancellationToken ct) =>
            await plans.UpdateAsync(
                id, req.Name, req.Limits, req.MonthlyPriceUsd, ct)
                is { } p ? Results.Ok(p) : Results.NotFound())
            .RequireAuthorization(PermManageSuperAdmins);

        g.MapDelete("/plans/{id:guid}", async (
            Guid id, IPlanAdminService plans, CancellationToken ct) =>
            await plans.DeleteAsync(id, ct) switch
            {
                null => Results.NoContent(),
                PlanDeleteError.NotFound => Results.NotFound(),
                _ => Results.Conflict(
                    new { detail = "plan has active references" }),
            }).RequireAuthorization(PermManageSuperAdmins);

        // Atribuição de plano à org: encerra a subscrição ativa e abre outra.
        g.MapPut("/organizations/{org_id:guid}/plan", async (
            Guid org_id, [FromBody] AssignPlanRequest req,
            IPlanAdminService plans, CancellationToken ct) =>
            await plans.AssignOrgPlanAsync(org_id, req.PlanId, ct)
                ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(PermManageSuperAdmins);

        // SPEC-14 RF-004: diagnostics ZIP — readiness, redacted logs, counts,
        // versions and effective config (secrets masked by construction).
        app.MapGet("/api/v1/support-bundle", async (
            OpenHandsNET.Infrastructure.Observability.SupportBundleService svc,
            CancellationToken ct) =>
        {
            var bytes = await svc.GenerateAsync(ct);
            return Results.File(bytes, "application/zip",
                $"openhandsnet-support-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");
        }).RequireAuthorization(PermManageSuperAdmins)
          .WithTags("Admin");

        return app;
    }

    /// <summary>Super-admin grant body.</summary>
    public sealed record GrantRequest(Guid? UserId, string? Email);

    /// <summary>Feature flag set body.</summary>
    public sealed record FlagRequest(bool Enabled, string? Description);

    /// <summary>Plan create body (limits serializam snake_case).</summary>
    public sealed record PlanCreateRequest(
        string Name, PlanLimits? Limits, decimal? MonthlyPriceUsd);

    /// <summary>Plan patch body — campos nulos preservam o valor atual.</summary>
    public sealed record PlanPatchRequest(
        string? Name, PlanLimits? Limits, decimal? MonthlyPriceUsd);

    /// <summary>Org plan assignment body.</summary>
    public sealed record AssignPlanRequest(Guid PlanId);
}