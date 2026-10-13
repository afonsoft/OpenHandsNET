using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Infrastructure.Identity;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>Instance admin endpoints: super-admins, users, audit, feature flags (SPEC-08 RF-008/011).</summary>
public static class AdminEndpoints
{
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
                ctx.User, null, "perm:manage_super_admins")).Succeeded
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
            var granted = req.UserId is { } uid
                ? await admins.GrantByUserIdAsync(uid, ct)
                : req.Email is { } email
                    ? await admins.GrantByEmailAsync(email, ct)
                    : null;
            return granted is null ? Results.NotFound() : Results.Ok(granted);
        }).RequireAuthorization("perm:manage_super_admins");

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
        }).RequireAuthorization("perm:manage_super_admins");

        // Admin users
        g.MapGet("/users", async (
            string? q, int? page,
            IAdminUserService users, CancellationToken ct) =>
            Results.Ok(await users.ListUsersAsync(
                q, page ?? 1, 50, ct)))
            .RequireAuthorization("perm:manage_super_admins");

        g.MapGet("/users/count", async (
            IAdminUserService users, CancellationToken ct) =>
            Results.Ok(new { count = await users.CountUsersAsync(ct) }))
            .RequireAuthorization("perm:manage_super_admins");

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
        }).RequireAuthorization("perm:manage_super_admins");

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
        }).RequireAuthorization("perm:manage_super_admins");

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

        return app;
    }

    /// <summary>Super-admin grant body.</summary>
    public sealed record GrantRequest(Guid? UserId, string? Email);

    /// <summary>Feature flag set body.</summary>
    public sealed record FlagRequest(bool Enabled, string? Description);
}
