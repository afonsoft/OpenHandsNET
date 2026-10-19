using Microsoft.AspNetCore.Mvc;

using OpenHandsNET.Api.Policies;
using OpenHandsNET.Application.Identity;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// Service users (SPEC-57): <c>/api/v1/orgs/{org_id}/service-users</c> +
/// emissão de chaves <c>svc_*</c> escopadas. Admin-only
/// (<c>perm:manage_api_keys</c> + scope <c>admin:service_users</c> quando
/// o ator é <c>svc_*</c>).
/// </summary>
public static class ServiceUserEndpoints
{
    /// <summary>Map service-user endpoints.</summary>
    public static IEndpointRouteBuilder MapServiceUserEndpoints(
        this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/orgs/{org_id:guid}/service-users");

        g.MapGet("", async (
            Guid org_id, IServiceUserService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(org_id, ct)))
            .RequireAuthorization("perm:manage_api_keys")
            .RequireScope(ApiScopes.AdminServiceUsers);

        g.MapPost("", async (
            Guid org_id,
            [FromBody] ServiceUserCreateRequest req,
            IServiceUserService svc, ICurrentUserAccessor current,
            IAuditService audit, CancellationToken ct) =>
        {
            try
            {
                var created = await svc.CreateAsync(
                    org_id, req, current.RequireUserId(), ct);
                await audit.RecordAsync(
                    "service_user.created",
                    current.User?.UserId.ToString(), org_id,
                    "service_user", created.Id.ToString(),
                    metadata: new { name = created.Name, role = created.Role },
                    cancellationToken: ct);
                return Results.Created(
                    $"/api/v1/orgs/{org_id}/service-users/{created.Id}",
                    created);
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { detail = e.Message });
            }
        }).RequireAuthorization("perm:manage_api_keys")
          .RequireScope(ApiScopes.AdminServiceUsers);

        g.MapPatch("/{id:guid}", async (
            Guid org_id, Guid id,
            [FromBody] ServiceUserUpdateRequest req,
            IServiceUserService svc, ICurrentUserAccessor current,
            IAuditService audit, CancellationToken ct) =>
        {
            try
            {
                var updated = await svc.UpdateAsync(org_id, id, req, ct);
                if (updated is null)
                    return Results.NotFound();
                await audit.RecordAsync(
                    "service_user.updated",
                    current.User?.UserId.ToString(), org_id,
                    "service_user", id.ToString(), cancellationToken: ct);
                return Results.Ok(updated);
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { detail = e.Message });
            }
        }).RequireAuthorization("perm:manage_api_keys")
          .RequireScope(ApiScopes.AdminServiceUsers);

        g.MapDelete("/{id:guid}", async (
            Guid org_id, Guid id,
            IServiceUserService svc, CancellationToken ct) =>
            await svc.DeleteAsync(org_id, id, ct)
                ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization("perm:manage_api_keys")
            .RequireScope(ApiScopes.AdminServiceUsers);

        g.MapPost("/{id:guid}/keys", async (
            Guid org_id, Guid id,
            [FromBody] ServiceUserKeyCreateRequest req,
            IServiceUserService svc, ICurrentUserAccessor current,
            IAuditService audit, CancellationToken ct) =>
        {
            try
            {
                var key = await svc.CreateKeyAsync(org_id, id, req, ct);
                await audit.RecordAsync(
                    "service_user.key_issued",
                    current.User?.UserId.ToString(), org_id,
                    "service_user", id.ToString(),
                    metadata: new
                    {
                        key_prefix = key.Key.Prefix,
                        scopes = req.Scopes,
                    },
                    cancellationToken: ct);
                // Plaintext uma única vez.
                return Results.Ok(new
                {
                    id = key.Key.Id,
                    prefix = key.Key.Prefix,
                    name = key.Key.Name,
                    org_id = key.Key.OrgId,
                    expires_at = key.Key.ExpiresAt,
                    key = key.Plaintext,
                });
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { detail = e.Message });
            }
        }).RequireAuthorization("perm:manage_api_keys")
          .RequireScope(ApiScopes.AdminServiceUsers);

        g.MapGet("/{id:guid}/keys", async (
            Guid org_id, Guid id,
            IServiceUserService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListKeysAsync(org_id, id, ct)))
            .RequireAuthorization("perm:manage_api_keys")
            .RequireScope(ApiScopes.AdminServiceUsers);

        g.MapDelete("/{id:guid}/keys/{key_id:guid}", async (
            Guid org_id, Guid id, Guid key_id,
            IServiceUserService svc, CancellationToken ct) =>
            await svc.DeleteKeyAsync(org_id, id, key_id, ct)
                ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization("perm:manage_api_keys")
            .RequireScope(ApiScopes.AdminServiceUsers);

        return app;
    }
}