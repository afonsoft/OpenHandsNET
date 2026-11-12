using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Identity;
using Sandcastle.Domain.Identity;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// SPEC-149 RF-001/RF-004: LLM routing surface —
/// <c>GET /api/v1/llm/routes</c> (provider×model catálogo),
/// <c>GET|PUT /api/v1/organizations/{org_id}/routing-rules</c>
/// (admin — <c>perm:edit_llm_settings</c>; allowlist internals nunca
/// saem para membros) e <c>GET /api/v1/usage/llm?by=role</c>
/// (breakdown role×model — mesmo escopo do /usage/budget).
/// </summary>
public static class LlmEndpoints
{
    private const string PermEditLlm = "perm:edit_llm_settings";

    /// <summary>Map LLM routing endpoints.</summary>
    public static IEndpointRouteBuilder MapLlmEndpoints(
        this IEndpointRouteBuilder app)
    {
        var llm = app.MapGroup("/api/v1/llm")
            .WithTags(EndpointTags.Api).RequireAuthorization();
        llm.MapGet("/routes", RoutesAsync);

        var orgs = app.MapGroup("/api/v1/organizations")
            .WithTags(EndpointTags.Api);
        orgs.MapGet("/{org_id:guid}/routing-rules", GetRulesAsync)
            .RequireAuthorization(PermEditLlm);
        orgs.MapPut("/{org_id:guid}/routing-rules", PutRulesAsync)
            .RequireAuthorization(PermEditLlm);

        var usage = app.MapGroup("/api/v1/usage")
            .WithTags(EndpointTags.Api).RequireAuthorization();
        usage.MapGet("/llm", GetLlmUsageAsync);

        return app;
    }

    /// <summary>Catálogo provider×model (SPEC-23 discovery — SPEC-149 expõe p/ o editor).</summary>
    private static async Task<IResult> RoutesAsync(
        IModelCatalog catalog, CancellationToken ct) =>
        Results.Ok(await catalog.ListAsync(ct));

    /// <summary>Documento atual da org (vazio ⇒ pass-through).</summary>
    private static async Task<IResult> GetRulesAsync(
        Guid org_id,
        IRoutingRulesService rules,
        CancellationToken ct) =>
        Results.Ok(await rules.GetAsync(org_id, ct)
            ?? new RoutingRulesDocument());

    /// <summary>PUT do documento — valida schema; null/vazio ⇒ limpa (pass-through).</summary>
    private static async Task<IResult> PutRulesAsync(
        Guid org_id,
        [FromBody] RoutingRulesDocument? body,
        ICurrentUserAccessor current,
        IRoutingRulesService rules,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            await rules.SetAsync(org_id, body,
                user.UserId.ToString(), ct);
        }
        catch (RoutingRulesException e)
        {
            return Results.BadRequest(new { detail = e.Message });
        }

        return Results.NoContent();
    }

    /// <summary>
    /// <c>?by=role</c> (único suportado) — caller scope por default;
    /// <c>?org={guid}</c> exige <c>view_billing</c> na org (admin+).
    /// </summary>
    private static async Task<IResult> GetLlmUsageAsync(
        [FromQuery] string? by,
        [FromQuery] string? org,
        ICurrentUserAccessor current,
        IUsageBudgetService budgets,
        IEffectiveOrgResolver resolver,
        AppDbContext db,
        CancellationToken ct)
    {
        if (by is not null && by != "role")
        {
            return Results.BadRequest(
                new { detail = "only by=role is supported" });
        }

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

            var role = user.ActorKind == ActorKind.ServiceUser
                ? user.Role
                : await db.OrgMembers.AsNoTracking()
                    .Where(m => m.OrgId == parsed
                        && m.UserId == user.UserId)
                    .Select(m => m.Role)
                    .FirstOrDefaultAsync(ct);
            if (!RolePermissions.ForOrgRole(role)
                    .Contains(Permission.ViewBilling))
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

        return Results.Ok(await budgets.GetLlmUsageAsync(
            user.UserId, orgId, orgScope, ct));
    }
}