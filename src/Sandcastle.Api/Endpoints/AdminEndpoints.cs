using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Identity;
using Sandcastle.Domain.Identity;
using Sandcastle.Infrastructure.Identity;

namespace Sandcastle.Api.Endpoints;

/// <summary>Instance admin endpoints: super-admins, users, audit, feature flags (SPEC-08 RF-008/011).</summary>
public static class AdminEndpoints
{
    private const string PermManageSuperAdmins = "perm:manage_super_admins";
    /// <summary>Map <c>/api/v1/admin</c> endpoints.</summary>
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/admin").WithTags("Sandcastle.Api");

        // Super admins — list open to any authenticated user when
        // USER_PROVISIONING_ENABLED (upstream OHE-3196 parity); writes are perm-gated.
        g.MapGet("/super-admins",
            ListSuperAdminsAsync)
            .RequireAuthorization();

        g.MapGet("/super-admins/me", GetSuperAdminsMeAsync).RequireAuthorization();

        g.MapPost("/super-admins", PostSuperAdminsAsync).RequireAuthorization(PermManageSuperAdmins);

        g.MapDelete("/super-admins/{uid:guid}", DeleteSuperAdminsUidGuidAsync).RequireAuthorization(PermManageSuperAdmins);


        // Admin users
        g.MapGet("/users", GetUsersAsync)
            .RequireAuthorization(PermManageSuperAdmins);

        g.MapGet("/users/count", GetUsersCountAsync)
            .RequireAuthorization(PermManageSuperAdmins);

        g.MapDelete("/users/{uid:guid}", DeleteUsersUidGuidAsync).RequireAuthorization(PermManageSuperAdmins);

        // Audit trail query (RF-011) + CSV/JSON export.
        g.MapGet("/audit",
            QueryAuditAsync)
            .RequireAuthorization(PermManageSuperAdmins);

        // Feature flags
        g.MapGet("/feature-flags", GetFeatureFlagsAsync)
            .RequireAuthorization("perm:manage_feature_flags");

        g.MapPut("/feature-flags/{name}", PutFeatureFlagsNameAsync).RequireAuthorization("perm:manage_feature_flags");

        g.MapDelete("/feature-flags/{name}", DeleteFeatureFlagsNameAsync)
            .RequireAuthorization("perm:manage_feature_flags");

        // SPEC-20 RF-005: catálogo de planos (limits em snake_case via
        // naming policy global). Delete 409 enquanto houver referência ativa.
        g.MapGet("/plans", GetPlansAsync)
            .RequireAuthorization(PermManageSuperAdmins);

        g.MapPost("/plans",
            CreatePlanAsync)
            .RequireAuthorization(PermManageSuperAdmins)
            .Produces<PlanInfo>(201)
            .ProducesProblem(400)
            .ProducesProblem(409);

        g.MapPatch("/plans/{id:guid}", PatchPlansIdGuidAsync)
            .RequireAuthorization(PermManageSuperAdmins);

        g.MapDelete("/plans/{id:guid}",
            DeletePlanAsync)
            .RequireAuthorization(PermManageSuperAdmins);

        // Atribuição de plano à org: encerra a subscrição ativa e abre outra.
        g.MapPut("/organizations/{org_id:guid}/plan", PutOrganizationsOrgIdGuidPlanAsync)
            .RequireAuthorization(PermManageSuperAdmins);

        // SPEC-14 RF-004: diagnostics ZIP — readiness, redacted logs, counts,
        // versions and effective config (secrets masked by construction).
        app.MapGet("/api/v1/support-bundle",
            GetSupportBundleAsync).RequireAuthorization(PermManageSuperAdmins)
          .WithTags("Admin");

        return app;
    }


    private static async Task<IResult> ListSuperAdminsAsync(
        ISuperAdminService admins, IConfiguration config,
        ICurrentUserAccessor current, HttpContext ctx,
        IAuthorizationService authz, CancellationToken ct)
    {
        if (current.User is null)
            return Results.Unauthorized();
        if (config.GetValue("Auth:UserProvisioningEnabled", false))
            return Results.Ok(await admins.ListAsync(ct));
        return (await authz.AuthorizeAsync(
            ctx.User, null, PermManageSuperAdmins)).Succeeded
            ? Results.Ok(await admins.ListAsync(ct))
            : Results.Forbid();
    }

    private static async Task<IResult> QueryAuditAsync(
        [AsParameters] AuditQueryParams p)
    {
        var (items, total) = await p.Audit.QueryAsync(
            p.User, p.Action, p.From, p.To, p.Page ?? 1, 100, p.Ct);
        if (string.Equals(p.Format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var csv = "id,actor_user_id,org_id,action,target_type,target_id,timestamp\n"
                + string.Join("\n", items.Select(e =>
                    $"{e.Id},{e.ActorUserId},{e.OrgId},{e.Action},{e.TargetType},{e.TargetId},{e.Timestamp:O}"));
            return Results.Text(csv, "text/csv");
        }
        return Results.Ok(new { items, total });
    }

    private static async Task<IResult> CreatePlanAsync(
        [FromBody] PlanCreateRequest req, IPlanAdminService plans,
        HttpContext http, CancellationToken ct)
    {
        // #218: `{}` deixa Name nulo — validar antes do service (NRE→500).
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "name é obrigatório");
        }
        return await plans.CreateAsync(
            req.Name, req.Limits ?? new(), req.MonthlyPriceUsd, ct)
            is { } p ? Results.Created($"/api/v1/admin/plans/{p.Id}", p)
            : SandcastleProblemFactory.Create(
                http, 409, "plan name already exists");
    }

    private static async Task<IResult> DeletePlanAsync(
        Guid id, IPlanAdminService plans, CancellationToken ct) =>
        await plans.DeleteAsync(id, ct) switch
        {
            null => Results.NoContent(),
            PlanDeleteError.NotFound => Results.NotFound(),
            _ => Results.Conflict(
                new { detail = "plan has active references" }),
        };

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

    /// <summary>Binding do audit query (SPEC-104 — AsParameters).</summary>
    private sealed class AuditQueryParams
    {
        [FromQuery(Name = "user")] public string? User { get; init; }
        [FromQuery(Name = "action")] public string? Action { get; init; }
        [FromQuery(Name = "from")] public DateTimeOffset? From { get; init; }
        [FromQuery(Name = "to")] public DateTimeOffset? To { get; init; }
        [FromQuery(Name = "page")] public int? Page { get; init; }
        [FromQuery(Name = "format")] public string? Format { get; init; }
        [FromServices] public IAuditService Audit { get; init; } = null!;
        public CancellationToken Ct { get; init; }
    }

    private static async Task<IResult> GetSuperAdminsMeAsync(ICurrentUserAccessor current, ISuperAdminService admins, CancellationToken ct)
    {
        var user = current.User;
        return user is null
            ? Results.Unauthorized()
            : Results.Ok(new
            {
                is_super_admin = await admins.IsSuperAdminAsync(user.UserId, ct),
            });

    }

    private static async Task<IResult> PostSuperAdminsAsync([FromBody] GrantRequest req, ICurrentUserAccessor current, ISuperAdminService admins, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        var granted = await GrantAsync(req, admins, ct);
        return granted is null ? Results.NotFound() : Results.Ok(granted);

    }

    private static async Task<IResult> DeleteSuperAdminsUidGuidAsync(Guid uid, ICurrentUserAccessor current, ISuperAdminService admins, CancellationToken ct)
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

    }

    private static async Task<IResult> GetUsersAsync(string? q, int? page, IAdminUserService users, CancellationToken ct)
    {
        return Results.Ok(await users.ListUsersAsync(
            q, page ?? 1, 50, ct));

    }

    private static async Task<IResult> GetUsersCountAsync(IAdminUserService users, CancellationToken ct)
    {
        return Results.Ok(new { count = await users.CountUsersAsync(ct) });

    }

    private static async Task<IResult> DeleteUsersUidGuidAsync(Guid uid, IAdminUserService users, CancellationToken ct)
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

    }

    private static async Task<IResult> GetFeatureFlagsAsync(IFeatureFlagService flags, CancellationToken ct)
    {
        return Results.Ok(await flags.ListAsync(ct));

    }

    private static async Task<IResult> PutFeatureFlagsNameAsync(string name, [FromBody] FlagRequest req, IFeatureFlagService flags, CancellationToken ct)
    {
        await flags.SetAsync(name, req.Enabled, req.Description, ct);
        return Results.NoContent();

    }

    private static async Task<IResult> DeleteFeatureFlagsNameAsync(string name, IFeatureFlagService flags, CancellationToken ct)
    {
        return await flags.DeleteAsync(name, ct)
            ? Results.NoContent() : Results.NotFound();

    }

    private static async Task<IResult> GetPlansAsync(IPlanAdminService plans, CancellationToken ct)
    {
        return Results.Ok(await plans.ListAsync(ct));

    }

    private static async Task<IResult> PatchPlansIdGuidAsync(Guid id, [FromBody] PlanPatchRequest req, IPlanAdminService plans, CancellationToken ct)
    {
        return await plans.UpdateAsync(
            id, req.Name, req.Limits, req.MonthlyPriceUsd, ct)
            is { } p ? Results.Ok(p) : Results.NotFound();

    }

    private static async Task<IResult> PutOrganizationsOrgIdGuidPlanAsync(Guid org_id, [FromBody] AssignPlanRequest req, IPlanAdminService plans, CancellationToken ct)
    {
        return await plans.AssignOrgPlanAsync(org_id, req.PlanId, ct)
            ? Results.NoContent() : Results.NotFound();

    }

    private static async Task<object?> GrantAsync(
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


    private static async Task<IResult> GetSupportBundleAsync(
        Sandcastle.Infrastructure.Observability.SupportBundleService svc,
        CancellationToken ct)
    {
        var bytes = await svc.GenerateAsync(ct);
        return Results.File(bytes, "application/zip",
            $"sandcastle-support-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");
    }

}