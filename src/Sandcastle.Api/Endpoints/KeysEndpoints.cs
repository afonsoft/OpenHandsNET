using Microsoft.AspNetCore.Mvc;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Identity;

namespace Sandcastle.Api.Endpoints;

/// <summary>API key management (SPEC-08 RF-002/RF-003).</summary>
public static class KeysEndpoints
{
    /// <summary>Map <c>/api/v1/keys</c> endpoints.</summary>
    public static IEndpointRouteBuilder MapKeysEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/keys");

        g.MapPost("", CreateApiKeyAsync)
            .WithTags(EndpointTags.Api)
            .RequireAuthorization("perm:manage_api_keys");

        g.MapGet("", (
            ICurrentUserAccessor current, IApiKeyService keys,
            CancellationToken ct) =>
            ListApiKeysAsync(current, keys, ct))
            .RequireAuthorization("perm:manage_api_keys");

        g.MapDelete("/{id:guid}", (
            Guid id, ICurrentUserAccessor current, IApiKeyService keys,
            IAuditService audit, CancellationToken ct) =>
            DeleteApiKeyAsync(id, current, keys, audit, ct))
            .RequireAuthorization("perm:manage_api_keys");

        // Bearer-only parity: /keys/current rejects cookie sessions (RF-002).
        g.MapGet("/current", (
            ICurrentUserAccessor current, IEffectiveOrgResolver resolver,
            HttpContext ctx, CancellationToken ct) =>
            CurrentAsync(current, resolver, ctx, ct))
            .RequireAuthorization();

        // SPEC-50 RF-002: BYOR LiteLLM — fail-closed: proxy não
        // configurado → 404 byor_not_configured; proxy indisponível →
        // 503 litellm_unavailable (nunca cai para providers locais).
        // A chave sai em claro SÓ no refresh.
        g.MapGet("/llm/byor",
            GetByorAsync).WithTags(EndpointTags.Api)
            .RequireAuthorization().ProducesProblem(404).ProducesProblem(503);

        g.MapPost("/llm/byor/refresh",
            RefreshByorAsync).WithTags(EndpointTags.Api)
            .RequireAuthorization().ProducesProblem(404).ProducesProblem(503);

        g.MapGet("/llm/byor/permitted",
            PermittedByorAsync).WithTags(EndpointTags.Api)
            .RequireAuthorization().ProducesProblem(404).ProducesProblem(503);

        g.MapDelete("/llm/byor",
            RevokeByorAsync).WithTags(EndpointTags.Api)
            .RequireAuthorization().ProducesProblem(404).ProducesProblem(503);

        // SPEC-134 RF-003/RF-004: BYOK por usuário — CRUD owner-scoped,
        // saída sempre mascarada (a key crua nunca sai), audit em
        // upsert/delete, test rate-limited no service.
        g.MapGet("/llm", async (
            ICurrentUserAccessor current, IUserLlmKeyService keys,
            CancellationToken ct) =>
        {
            var user = current.User;
            return user is null
                ? Results.Unauthorized()
                : Results.Ok(await keys.ListAsync(
                    user.UserId.ToString(), ct));
        }).WithTags(EndpointTags.Api).RequireAuthorization();

        g.MapGet("/llm/providers", (
            IUserLlmKeyService keys) => Results.Ok(keys.Providers))
            .WithTags(EndpointTags.Api).RequireAuthorization();

        g.MapPut("/llm/{provider}", (
            string provider, [FromBody] UserLlmKeyUpsertRequest req,
            ICurrentUserAccessor current, IUserLlmKeyService keys,
            IAuditService audit, CancellationToken ct) =>
            UpsertLlmKeyAsync(provider, req, current, keys, audit, ct))
            .WithTags(EndpointTags.Api).RequireAuthorization();

        g.MapDelete("/llm/{provider}", (
            string provider,
            ICurrentUserAccessor current, IUserLlmKeyService keys,
            IAuditService audit, CancellationToken ct) =>
            DeleteLlmKeyAsync(provider, current, keys, audit, ct))
            .WithTags(EndpointTags.Api).RequireAuthorization();

        g.MapPost("/llm/{provider}/test", (
            string provider,
            ICurrentUserAccessor current, IUserLlmKeyService keys,
            CancellationToken ct) =>
            TestLlmKeyAsync(provider, current, keys, ct))
            .WithTags(EndpointTags.Api).RequireAuthorization();

        return app;
    }


    private static async Task<IResult> ListApiKeysAsync(
        ICurrentUserAccessor current, IApiKeyService keys,
        CancellationToken ct)
    {
        var user = current.User;
        return user is null
            ? Results.Unauthorized()
            : Results.Ok(await keys.ListAsync(user, ct));
    }

    private static async Task<IResult> DeleteApiKeyAsync(
        Guid id, ICurrentUserAccessor current, IApiKeyService keys,
        IAuditService audit, CancellationToken ct)
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
    }

    private static async Task<IResult> UpsertLlmKeyAsync(
        string provider, [FromBody] UserLlmKeyUpsertRequest req,
        ICurrentUserAccessor current, IUserLlmKeyService keys,
        IAuditService audit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        try
        {
            var info = await keys.UpsertAsync(
                user.UserId.ToString(), provider, req.Key,
                req.BaseUrl, ct);
            await audit.RecordAsync(
                "keys.llm.upsert", user.UserId.ToString(), null,
                "user_llm_key", provider, cancellationToken: ct);
            return Results.Ok(info);
        }
        catch (LlmKeyValidationException e)
        {
            return Results.BadRequest(new { detail = e.Code });
        }
    }

    private static async Task<IResult> DeleteLlmKeyAsync(
        string provider,
        ICurrentUserAccessor current, IUserLlmKeyService keys,
        IAuditService audit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        if (!await keys.DeleteAsync(
                user.UserId.ToString(), provider, ct))
            return Results.NotFound();
        await audit.RecordAsync(
            "keys.llm.delete", user.UserId.ToString(), null,
            "user_llm_key", provider, cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> TestLlmKeyAsync(
        string provider,
        ICurrentUserAccessor current, IUserLlmKeyService keys,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        try
        {
            var (ok, detail) = await keys.TestAsync(
                user.UserId.ToString(), provider, ct);
            return detail == "rate_limited"
                ? Results.Problem(statusCode: 429, title: "rate_limited")
                : Results.Ok(new { ok, detail });
        }
        catch (LlmKeyValidationException e)
        {
            return Results.BadRequest(new { detail = e.Code });
        }
    }

    private static async Task<IResult> CreateApiKeyAsync(
        [FromBody] ApiKeyCreateRequest req,
        ICurrentUserAccessor current, IApiKeyService keys,
        IAuditService audit, CancellationToken ct)
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
    }

    private static async Task<IResult> GetByorAsync(
        ICurrentUserAccessor current, IByorKeyService byor,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        if (!byor.Enabled)
            return ByorNotConfigured();
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
    }

    private static async Task<IResult> RefreshByorAsync(
        [FromBody] ByorRefreshRequest? req,
        ICurrentUserAccessor current, IByorKeyService byor,
        IEffectiveOrgResolver resolver, IAuditService audit,
        HttpContext ctx, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        if (!byor.Enabled)
            return ByorNotConfigured();
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
    }

    private static async Task<IResult> PermittedByorAsync(
        ICurrentUserAccessor current, IByorKeyService byor,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        if (!byor.Enabled)
            return ByorNotConfigured();
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
    }

    private static async Task<IResult> RevokeByorAsync(
        ICurrentUserAccessor current, IByorKeyService byor,
        IAuditService audit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        if (!byor.Enabled)
            return ByorNotConfigured();
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
    }

    private static IResult ByorNotConfigured() => Results.Problem(
        statusCode: 404, title: "byor_not_configured",
        detail: "LiteLLM proxy is not configured for this deployment",
        extensions: new Dictionary<string, object?>
        {
            ["code"] = "byor_not_configured",
        });

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
            // SPEC-57 RF-004: identidade do ator autenticado.
            actor_kind = user.ActorKind == ActorKind.ServiceUser
                ? "service_user" : "user",
            scopes = user.Scopes,
            role = user.Role,
        });
    }

    /// <summary>Create request body.</summary>
    public sealed record ApiKeyCreateRequest(
        string? Name, DateTimeOffset? NotBefore,
        DateTimeOffset? ExpiresAt, Guid? OrgId);

    /// <summary>BYOR refresh body (SPEC-50) — budget override opcional.</summary>
    public sealed record ByorRefreshRequest(decimal? Budget);

    /// <summary>BYOK upsert body (SPEC-134) — key + base_url opcional.</summary>
    public sealed record UserLlmKeyUpsertRequest(
        string Key, string? BaseUrl);
}