using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Identity;
using Sandcastle.Domain.Identity;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// SPEC-147 RF-004: <c>GET /api/v1/usage/budget</c> — budget do caller
/// (default) ou da org com <c>?org={guid}</c> (exige
/// <c>perm:view_billing</c> na org alvo — admin+).
/// </summary>
public static class UsageEndpoints
{
    /// <summary>Map <c>/api/v1/usage</c> endpoints.</summary>
    public static IEndpointRouteBuilder MapUsageEndpoints(
        this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/usage")
            .WithTags(EndpointTags.Api)
            .RequireAuthorization();

        g.MapGet("/budget", GetBudgetAsync);

        return app;
    }

    private static async Task<IResult> GetBudgetAsync(
        [FromQuery] string? org,
        ICurrentUserAccessor current,
        IUsageBudgetService budgets,
        IEffectiveOrgResolver resolver,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }

        Guid? orgId;
        var orgScope = false;
        if (org is not null)
        {
            if (!Guid.TryParse(org, out var parsed))
            {
                return Results.BadRequest(new { detail = "invalid org" });
            }

            // Org view — mesma avaliação do PermissionHandler (role da
            // org → perm:view_billing, superadmin via ServiceUser role).
            var role = user.ActorKind == ActorKind.ServiceUser
                ? user.Role
                : await db.OrgMembers.AsNoTracking()
                    .Where(m => m.OrgId == parsed && m.UserId == user.UserId)
                    .Select(m => m.Role)
                    .FirstOrDefaultAsync(ct);
            var allowed = RolePermissions.ForOrgRole(role)
                .Contains(Permission.ViewBilling);
            if (!allowed)
            {
                return Results.Forbid();
            }

            orgId = parsed;
            orgScope = true;
        }
        else
        {
            orgId = (await resolver.ResolveAsync(user, null, ct))?.OrgId;
        }

        return Results.Ok(await budgets.GetBudgetAsync(
            user.UserId, orgId, orgScope, ct));
    }
}