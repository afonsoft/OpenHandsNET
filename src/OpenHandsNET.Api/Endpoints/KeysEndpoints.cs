using Microsoft.AspNetCore.Mvc;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Identity;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>API key management (SPEC-08 RF-002/RF-003).</summary>
public static class KeysEndpoints
{
    /// <summary>Map <c>/api/v1/keys</c> endpoints.</summary>
    public static IEndpointRouteBuilder MapKeysEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/keys");

        g.MapPost("", async (
            [FromBody] ApiKeyCreateRequest req,
            ICurrentUserAccessor current, IApiKeyService keys,
            IAuditService audit, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            try
            {
                var result = await keys.CreateAsync(
                    user, req.Name, req.NotBefore, req.ExpiresAt, req.OrgId, ct);
                await audit.RecordAsync(
                    "api_key.create", user.UserId.ToString(), req.OrgId,
                    "api_key", result.Key.Id.ToString(),
                    cancellationToken: ct);
                // Plaintext returned exactly once.
                return Results.Ok(new
                {
                    id = result.Key.Id,
                    prefix = result.Key.Prefix,
                    name = result.Key.Name,
                    org_id = result.Key.OrgId,
                    expires_at = result.Key.ExpiresAt,
                    key = result.Plaintext,
                });
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { detail = e.Message });
            }
        }).RequireAuthorization("perm:manage_api_keys");

        g.MapGet("", async (
            ICurrentUserAccessor current, IApiKeyService keys, CancellationToken ct) =>
        {
            var user = current.User;
            return user is null
                ? Results.Unauthorized()
                : Results.Ok(await keys.ListAsync(user, ct));
        }).RequireAuthorization("perm:manage_api_keys");

        g.MapDelete("/{id:guid}", async (
            Guid id, ICurrentUserAccessor current, IApiKeyService keys,
            IAuditService audit, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            if (!await keys.DeleteAsync(user, id, ct))
                return Results.NotFound();
            await audit.RecordAsync(
                "api_key.delete", user.UserId.ToString(), null,
                "api_key", id.ToString(), cancellationToken: ct);
            return Results.NoContent();
        }).RequireAuthorization("perm:manage_api_keys");

        // Bearer-only parity: /keys/current rejects cookie sessions (RF-002).
        g.MapGet("/current", (
            ICurrentUserAccessor current, IEffectiveOrgResolver resolver,
            HttpContext ctx, CancellationToken ct) =>
            CurrentAsync(current, resolver, ctx, ct))
            .RequireAuthorization();

        // SPEC-50 RF-002: BYOR LiteLLM — fail-closed: proxy não
        // configurado/indisponível → 503 litellm_unavailable (nunca
        // cai para providers locais). A chave sai em claro SÓ no refresh.
        g.MapGet("/llm/byor", async (
            ICurrentUserAccessor current, IByorKeyService byor,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            if (!byor.Enabled)
                return ProxyUnavailable();
            try
            {
                var info = await byor.GetActiveAsync(
                    user.UserId.ToString(), ct);
                return Results.Ok(new
                {
                    configured = info is not null,
                    key_prefix = info?.KeyPrefix,
                    key_id = info?.KeyId,
                    budget = info?.BudgetUsd,
                    models = info?.Models ?? [],
                    created_at = info?.CreatedAt,
                });
            }
            catch (LiteLlmUnavailableException)
            {
                return ProxyUnavailable();
            }
        }).RequireAuthorization();

        g.MapPost("/llm/byor/refresh", async (
            [FromBody] ByorRefreshRequest? req,
            ICurrentUserAccessor current, IByorKeyService byor,
            IEffectiveOrgResolver resolver, IAuditService audit,
            HttpContext ctx, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            if (!byor.Enabled)
                return ProxyUnavailable();
            try
            {
                var effective = await resolver.ResolveAsync(
                    user, ctx.Request.Headers["X-Org-Id"].ToString(), ct);
                var (key, info) = await byor.RefreshAsync(
                    user.UserId.ToString(), effective?.OrgId.ToString(),
                    req?.Budget, ct);
                await audit.RecordAsync(
                    "byor_key.refresh", user.UserId.ToString(),
                    effective?.OrgId, "byor_key", info.Id.ToString(),
                    cancellationToken: ct);
                // Plaintext retorna exatamente uma vez.
                return Results.Ok(new
                {
                    key,
                    key_id = info.KeyId,
                    key_prefix = info.KeyPrefix,
                    budget = info.BudgetUsd,
                    models = info.Models,
                });
            }
            catch (LiteLlmUnavailableException)
            {
                return ProxyUnavailable();
            }
        }).RequireAuthorization();

        g.MapGet("/llm/byor/permitted", async (
            ICurrentUserAccessor current, IByorKeyService byor,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            if (!byor.Enabled)
                return ProxyUnavailable();
            try
            {
                return Results.Ok(new
                {
                    models = await byor.PermittedModelsAsync(ct),
                });
            }
            catch (LiteLlmUnavailableException)
            {
                return ProxyUnavailable();
            }
        }).RequireAuthorization();

        g.MapDelete("/llm/byor", async (
            ICurrentUserAccessor current, IByorKeyService byor,
            IAuditService audit, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            if (!byor.Enabled)
                return ProxyUnavailable();
            try
            {
                if (!await byor.RevokeAsync(user.UserId.ToString(), ct))
                    return Results.NotFound();
                await audit.RecordAsync(
                    "byor_key.delete", user.UserId.ToString(), null,
                    "byor_key", null, cancellationToken: ct);
                return Results.NoContent();
            }
            catch (LiteLlmUnavailableException)
            {
                return ProxyUnavailable();
            }
        }).RequireAuthorization();

        return app;
    }

    private static IResult ProxyUnavailable() => Results.Problem(
        statusCode: 503, title: "litellm_unavailable",
        detail: "LiteLLM proxy is not configured or unreachable",
        extensions: new Dictionary<string, object?>
        {
            ["code"] = "litellm_unavailable",
        });

    private static async Task<IResult> CurrentAsync(
        ICurrentUserAccessor current, IEffectiveOrgResolver resolver,
        HttpContext ctx, CancellationToken ct)
    {
        var user = current.User;
        if (user is null || user.AuthType != AuthType.Bearer)
            return Results.Unauthorized();
        var effective = await resolver.ResolveAsync(
            user, ctx.Request.Headers["X-Org-Id"].ToString(), ct);
        return Results.Ok(new
        {
            org_id = effective?.OrgId,
            bound_org_id = user.BoundOrgId,
            auth_type = "bearer",
        });
    }

    /// <summary>Create request body.</summary>
    public sealed record ApiKeyCreateRequest(
        string? Name, DateTimeOffset? NotBefore,
        DateTimeOffset? ExpiresAt, Guid? OrgId);

    /// <summary>BYOR refresh body (SPEC-50) — budget override opcional.</summary>
    public sealed record ByorRefreshRequest(decimal? Budget);
}
