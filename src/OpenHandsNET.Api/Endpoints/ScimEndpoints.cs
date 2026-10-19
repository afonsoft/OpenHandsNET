using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Api.Policies;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// SPEC-58: SCIM 2.0 subset (RFC 7643/7644) + admin de idp-mappings,
/// usage-tiers e overrides. Bearer <c>scim_*</c> resolve a org do token;
/// respostas em <c>application/scim+json</c> com erro SCIM padrão.
/// </summary>
public static class ScimEndpoints
{
    private const string ScimJson = "application/scim+json";
    private const string ErrorSchema =
        "urn:ietf:params:scim:api:messages:2.0:Error";

    public static IEndpointRouteBuilder MapScimEndpoints(
        this IEndpointRouteBuilder app)
    {
        var scim = app.MapGroup("/scim/v2");

        // ---------- discovery (estático) ----------
        scim.MapGet("/ServiceProviderConfig", () => ScimOk(new JsonObject
        {
            ["schemas"] = new JsonArray(
                "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig"),
            ["patch"] = new JsonObject { ["supported"] = true },
            ["bulk"] = new JsonObject { ["supported"] = false },
            ["filter"] = new JsonObject
            {
                ["supported"] = true,
                ["maxResults"] = 200,
            },
            ["authenticationSchemes"] = new JsonArray(new JsonObject
            {
                ["type"] = "oauthbearertoken",
                ["name"] = "scim_*",
                ["primary"] = true,
            }),
        }));
        scim.MapGet("/ResourceTypes", () => ScimOk(new JsonObject
        {
            ["totalResults"] = 2,
            ["Resources"] = new JsonArray(
                ResourceType("User", "/scim/v2/Users"),
                ResourceType("Group", "/scim/v2/Groups")),
        }));
        scim.MapGet("/Schemas", () => ScimOk(new JsonObject
        {
            ["totalResults"] = 2,
            ["Resources"] = new JsonArray(
                "urn:ietf:params:scim:schemas:core:2.0:User",
                "urn:ietf:params:scim:schemas:core:2.0:Group"),
        }));

        // ---------- Users ----------
        scim.MapGet("/Users", async (HttpContext ctx,
            IScimTokenService tokens, AppDbContext db, string? filter,
            CancellationToken ct) =>
        {
            var orgId = await ResolveOrg(ctx, tokens, ct);
            if (orgId is null)
                return ScimError(401, "unauthorized");
            var userName = ParseUserNameFilter(filter);
            if (filter is not null && userName is null)
                return ScimError(400, "invalidFilter", "invalidFilter");
            var q = db.Users.AsNoTracking()
                .Join(db.OrgMembers.AsNoTracking(),
                    u => u.Id, m => m.UserId, (u, m) => new { u, m })
                .Where(x => x.m.OrgId == orgId);
            if (userName is not null)
                q = q.Where(x => x.u.Email == userName);
            var users = await q.Take(200).ToListAsync(ct);
            return ScimOk(new JsonObject
            {
                ["schemas"] = new JsonArray(
                    "urn:ietf:params:scim:api:messages:2.0:ListResponse"),
                ["totalResults"] = users.Count,
                ["Resources"] = new JsonArray(
                    users.Select(x => ToScimUser(x.u, x.m)).ToArray()),
            });
        }).DisableAntiforgery();

        scim.MapGet("/Users/{id:guid}", async (HttpContext ctx,
            IScimTokenService tokens, AppDbContext db, Guid id,
            CancellationToken ct) =>
        {
            var orgId = await ResolveOrg(ctx, tokens, ct);
            if (orgId is null)
                return ScimError(401, "unauthorized");
            var row = await db.Users.AsNoTracking()
                .Join(db.OrgMembers.AsNoTracking(),
                    u => u.Id, m => m.UserId, (u, m) => new { u, m })
                .FirstOrDefaultAsync(
                    x => x.u.Id == id && x.m.OrgId == orgId, ct);
            return row is null
                ? ScimError(404, $"user {id} not found")
                : ScimOk(ToScimUser(row.u, row.m));
        });

        scim.MapPost("/Users", async (HttpContext ctx,
            IScimTokenService tokens, AppDbContext db, IAuditService audit,
            CancellationToken ct) =>
        {
            var orgId = await ResolveOrg(ctx, tokens, ct);
            if (orgId is null)
                return ScimError(401, "unauthorized");
            var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var userName = body?["userName"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(userName))
                return ScimError(400, "userName required", "invalidValue");
            var active = body?["active"]?.GetValue<bool>() ?? true;
            var displayName = body?["displayName"]?.GetValue<string>();

            var existing = await db.Users.FirstOrDefaultAsync(
                u => u.Email == userName, ct);
            if (existing is not null
                && await db.OrgMembers.AnyAsync(
                    m => m.OrgId == orgId && m.UserId == existing.Id, ct))
            {
                return ScimError(409, "userName already provisioned",
                    "uniqueness");
            }

            var user = existing ?? new UserRecord
            {
                Id = Guid.NewGuid(),
                Email = userName,
                DisplayName = displayName,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            if (existing is null)
                db.Users.Add(user);
            if (displayName is not null)
                user.DisplayName = displayName;
            if (!active)
                user.DisabledAt ??= DateTimeOffset.UtcNow;

            if (existing is null || !await db.OrgMembers.AnyAsync(
                    m => m.OrgId == orgId && m.UserId == user.Id, ct))
            {
                db.OrgMembers.Add(new OrgMemberRecord
                {
                    OrgId = orgId.Value,
                    UserId = user.Id,
                    Role = "member",
                    Source = "idp",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync(
                "scim.user_provisioned", "scim", orgId, "user",
                user.Id.ToString());
            var member = await db.OrgMembers.AsNoTracking().FirstAsync(
                m => m.OrgId == orgId && m.UserId == user.Id, ct);
            return Results.Json(
                ToScimUser(user, member), statusCode: 201,
                contentType: ScimJson);
        }).DisableAntiforgery();

        scim.MapMethods("/Users/{id:guid}", ["PATCH"], async (
            HttpContext ctx, IScimTokenService tokens, AppDbContext db,
            IAuditService audit, Guid id, CancellationToken ct) =>
        {
            var orgId = await ResolveOrg(ctx, tokens, ct);
            if (orgId is null)
                return ScimError(401, "unauthorized");
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null || !await db.OrgMembers.AnyAsync(
                    m => m.OrgId == orgId && m.UserId == id, ct))
                return ScimError(404, $"user {id} not found");
            var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var ops = body?["Operations"] as JsonArray;
            var active = ExtractActive(body, ops);
            if (active is null)
                return ScimError(400, "no supported operation",
                    "invalidSyntax");
            if (active == false)
            {
                user.DisabledAt ??= DateTimeOffset.UtcNow;
                await audit.RecordAsync(
                    "scim.user_deprovisioned", "scim", orgId, "user",
                    id.ToString());
            }
            else
            {
                user.DisabledAt = null;
            }
            await db.SaveChangesAsync(ct);
            var member = await db.OrgMembers.AsNoTracking().FirstAsync(
                m => m.OrgId == orgId && m.UserId == id, ct);
            return ScimOk(ToScimUser(user, member));
        });

        scim.MapDelete("/Users/{id:guid}", async (HttpContext ctx,
            IScimTokenService tokens, AppDbContext db, IAuditService audit,
            Guid id, CancellationToken ct) =>
        {
            var orgId = await ResolveOrg(ctx, tokens, ct);
            if (orgId is null)
                return ScimError(401, "unauthorized");
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null || !await db.OrgMembers.AnyAsync(
                    m => m.OrgId == orgId && m.UserId == id, ct))
                return ScimError(404, $"user {id} not found");
            user.DisabledAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync(
                "scim.user_deprovisioned", "scim", orgId, "user",
                id.ToString());
            return Results.NoContent();
        });

        // ---------- Groups (displayName → idp_group_mapping) ----------
        scim.MapGet("/Groups", async (HttpContext ctx,
            IScimTokenService tokens, AppDbContext db,
            CancellationToken ct) =>
        {
            var orgId = await ResolveOrg(ctx, tokens, ct);
            if (orgId is null)
                return ScimError(401, "unauthorized");
            var groups = await db.IdpGroupMappings.AsNoTracking()
                .Where(m => m.OrgId == orgId).ToListAsync(ct);
            var arr = new JsonArray();
            foreach (var g in groups)
                arr.Add(await ToScimGroupAsync(db, g, ct));
            return ScimOk(new JsonObject
            {
                ["schemas"] = new JsonArray(
                    "urn:ietf:params:scim:api:messages:2.0:ListResponse"),
                ["totalResults"] = groups.Count,
                ["Resources"] = arr,
            });
        });

        scim.MapPost("/Groups", async (HttpContext ctx,
            IScimTokenService tokens, AppDbContext db,
            CancellationToken ct) =>
        {
            var orgId = await ResolveOrg(ctx, tokens, ct);
            if (orgId is null)
                return ScimError(401, "unauthorized");
            var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var displayName = body?["displayName"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(displayName))
                return ScimError(400, "displayName required", "invalidValue");
            if (await db.IdpGroupMappings.AnyAsync(
                    m => m.OrgId == orgId && m.IdpGroup == displayName, ct))
                return ScimError(409, "group exists", "uniqueness");
            var rec = new IdpGroupMappingRecord
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                IdpGroup = displayName,
                Role = "member",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.IdpGroupMappings.Add(rec);
            await db.SaveChangesAsync(ct);
            await SyncGroupMembersAsync(
                db, rec, body?["members"] as JsonArray, ct);
            await db.SaveChangesAsync(ct);
            return Results.Json(
                await ToScimGroupAsync(db, rec, ct),
                statusCode: 201, contentType: ScimJson);
        }).DisableAntiforgery();

        scim.MapPut("/Groups/{id:guid}", async (HttpContext ctx,
            IScimTokenService tokens, AppDbContext db, Guid id,
            CancellationToken ct) =>
        {
            var orgId = await ResolveOrg(ctx, tokens, ct);
            if (orgId is null)
                return ScimError(401, "unauthorized");
            var rec = await db.IdpGroupMappings.FirstOrDefaultAsync(
                m => m.Id == id && m.OrgId == orgId, ct);
            if (rec is null)
                return ScimError(404, $"group {id} not found");
            var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var displayName = body?["displayName"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(displayName))
                rec.IdpGroup = displayName;
            // PUT = replace members: remove idp-sourced memberships criadas
            // por este grupo e re-aplica a lista enviada.
            await SyncGroupMembersAsync(
                db, rec, body?["members"] as JsonArray, ct,
                replace: true);
            await db.SaveChangesAsync(ct);
            return ScimOk(await ToScimGroupAsync(db, rec, ct));
        });

        scim.MapDelete("/Groups/{id:guid}", async (HttpContext ctx,
            IScimTokenService tokens, AppDbContext db, Guid id,
            CancellationToken ct) =>
        {
            var orgId = await ResolveOrg(ctx, tokens, ct);
            if (orgId is null)
                return ScimError(401, "unauthorized");
            var n = await db.IdpGroupMappings
                .Where(m => m.Id == id && m.OrgId == orgId)
                .ExecuteDeleteAsync(ct);
            return n > 0
                ? Results.NoContent()
                : ScimError(404, $"group {id} not found");
        });

        // ---------- admin API (cookie/sk-*, perm) ----------
        var admin = app.MapGroup("/api/v1/orgs/{org_id:guid}");

        admin.MapGet("/idp-mappings", async (
            Guid org_id, IIdentityAdminService svc, CancellationToken ct) =>
            Results.Ok(new { mappings = await svc.ListMappingsAsync(org_id, ct) }))
            .RequireAuthorization("perm:view_org_settings");

        admin.MapPost("/idp-mappings", async (
            Guid org_id, HttpContext ctx, IIdentityAdminService svc,
            CancellationToken ct) =>
        {
            var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var group = body?["idp_group"]?.GetValue<string>()
                ?? body?["idpGroup"]?.GetValue<string>();
            var role = body?["role"]?.GetValue<string>() ?? "member";
            Guid? tier = body?["usage_tier_id"] is JsonNode tn
                && Guid.TryParse(tn.GetValue<string>(), out var t) ? t : null;
            if (string.IsNullOrWhiteSpace(group))
                return Results.BadRequest(new { error = "idp_group required" });
            try
            {
                return Results.Ok(await svc.CreateMappingAsync(
                    org_id, group, role, tier, ct));
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        }).RequireAuthorization("perm:edit_org_settings");

        admin.MapDelete("/idp-mappings/{id:guid}", async (
            Guid org_id, Guid id, IIdentityAdminService svc,
            CancellationToken ct) =>
            await svc.DeleteMappingAsync(org_id, id, ct)
                ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization("perm:edit_org_settings");

        admin.MapGet("/usage-tiers", async (
            Guid org_id, IIdentityAdminService svc, CancellationToken ct) =>
            Results.Ok(new { tiers = await svc.ListTiersAsync(org_id, ct) }))
            .RequireAuthorization("perm:view_org_settings");

        admin.MapPost("/usage-tiers", async (
            Guid org_id, HttpContext ctx, IIdentityAdminService svc,
            CancellationToken ct) =>
        {
            var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var name = body?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "name required" });
            var limits = body?["limits_json"]?.GetValue<string>()
                ?? body?["limits"]?.ToJsonString();
            return Results.Ok(await svc.CreateTierAsync(
                org_id, name, limits, ct));
        }).RequireAuthorization("perm:edit_org_settings");

        admin.MapPatch("/usage-tiers/{id:guid}", async (
            Guid org_id, Guid id, HttpContext ctx,
            IIdentityAdminService svc, CancellationToken ct) =>
        {
            var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var r = await svc.UpdateTierAsync(
                org_id, id,
                body?["name"]?.GetValue<string>(),
                body?["limits_json"]?.GetValue<string>()
                    ?? body?["limits"]?.ToJsonString(), ct);
            return r is null ? Results.NotFound() : Results.Ok(r);
        }).RequireAuthorization("perm:edit_org_settings");

        admin.MapDelete("/usage-tiers/{id:guid}", async (
            Guid org_id, Guid id, IIdentityAdminService svc,
            CancellationToken ct) =>
            await svc.DeleteTierAsync(org_id, id, ct)
                ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization("perm:edit_org_settings");

        // scim_* token mint/revoke (admin)
        admin.MapGet("/scim-tokens", async (
            Guid org_id, IScimTokenService tokens, CancellationToken ct) =>
            Results.Ok(new
            {
                tokens = (await tokens.ListAsync(org_id, ct))
                    .Select(t => new { t.Id, t.Prefix, t.Name, t.CreatedAt }),
            })).RequireAuthorization("perm:manage_api_keys");

        admin.MapPost("/scim-tokens", async (
            Guid org_id, HttpContext ctx, IScimTokenService tokens,
            CancellationToken ct) =>
        {
            string? name = null;
            if (ctx.Request.ContentLength > 0)
            {
                var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                name = body?["name"]?.GetValue<string>();
            }
            var issue = await tokens.MintAsync(org_id, name, ct);
            return Results.Ok(new
            {
                id = issue.Id,
                prefix = issue.Prefix,
                token = issue.Plaintext,
            });
        }).RequireAuthorization("perm:manage_api_keys")
            .RequireScope(ApiScopes.AdminServiceUsers);

        admin.MapDelete("/scim-tokens/{id:guid}", async (
            Guid org_id, Guid id, IScimTokenService tokens,
            CancellationToken ct) =>
            await tokens.RevokeAsync(org_id, id, ct)
                ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization("perm:manage_api_keys")
            .RequireScope(ApiScopes.AdminServiceUsers);

        // usage override por usuário (admin)
        app.MapPost("/api/v1/users/{id:guid}/usage-override", async (
            Guid id, HttpContext ctx, IIdentityAdminService svc,
            ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
            CancellationToken ct) =>
        {
            var actor = current.User;
            if (actor is null)
                return Results.Unauthorized();
            var org = await orgs.ResolveAsync(actor, ctx.Request.Headers["X-Org-Id"].ToString(), ct);
            var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            DateTimeOffset? expires = body?["expires_at"] is JsonNode en
                && DateTimeOffset.TryParse(
                    en.GetValue<string>(), out var ex) ? ex : null;
            var r = await svc.SetUserOverrideAsync(
                org?.OrgId ?? Guid.Empty, id, actor.UserId,
                body?["limits_json"]?.GetValue<string>()
                    ?? body?["limits"]?.ToJsonString(), expires, ct);
            return Results.Ok(r);
        }).RequireAuthorization("perm:edit_org_settings");

        app.MapDelete("/api/v1/users/{id:guid}/usage-override", async (
            Guid id, HttpContext ctx, IIdentityAdminService svc,
            IEffectiveOrgResolver orgs, ICurrentUserAccessor current,
            CancellationToken ct) =>
        {
            var actor = current.User;
            if (actor is null)
                return Results.Unauthorized();
            var org = await orgs.ResolveAsync(actor, ctx.Request.Headers["X-Org-Id"].ToString(), ct);
            return await svc.ClearUserOverrideAsync(
                    org?.OrgId ?? Guid.Empty, id, ct)
                ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization("perm:edit_org_settings");

        return app;
    }

    private static async Task<Guid?> ResolveOrg(
        HttpContext ctx, IScimTokenService tokens, CancellationToken ct)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
            return null;
        return await tokens.ValidateAsync(header[7..].Trim(), ct);
    }

    private static string? ParseUserNameFilter(string? filter)
    {
        if (filter is null)
            return null;
        // subset: userName eq "x" (única forma suportada)
        var m = System.Text.RegularExpressions.Regex.Match(
            filter, "userName\\s+eq\\s+\"([^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool? ExtractActive(JsonNode? body, JsonArray? ops)
    {
        if (body?["active"] is JsonNode direct)
            return direct.GetValue<bool>();
        foreach (var op in ops ?? [])
        {
            var v = op?["value"]?["active"];
            if (v is not null)
                return v.GetValue<bool>();
        }
        return null;
    }

    private static async Task SyncGroupMembersAsync(
        AppDbContext db, IdpGroupMappingRecord group,
        JsonArray? members, CancellationToken ct, bool replace = false)
    {
        var orgId = group.OrgId!.Value;
        var desired = new HashSet<Guid>();
        foreach (var m in members ?? [])
        {
            if (Guid.TryParse(m?["value"]?.GetValue<string>(), out var uid))
                desired.Add(uid);
        }
        if (replace)
        {
            var stale = await db.OrgMembers
                .Where(x => x.OrgId == orgId && x.Source == "idp"
                    && !desired.Contains(x.UserId))
                .ToListAsync(ct);
            db.OrgMembers.RemoveRange(stale);
        }
        foreach (var uid in desired)
        {
            if (await db.Users.AnyAsync(u => u.Id == uid, ct)
                && !await db.OrgMembers.AnyAsync(
                    x => x.OrgId == orgId && x.UserId == uid, ct))
            {
                db.OrgMembers.Add(new OrgMemberRecord
                {
                    OrgId = orgId,
                    UserId = uid,
                    Role = group.Role,
                    Source = "idp",
                    UsageTierId = group.UsageTierId,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
        }
    }

    private static async Task<JsonObject> ToScimGroupAsync(
        AppDbContext db, IdpGroupMappingRecord g, CancellationToken ct)
    {
        var memberIds = await db.OrgMembers.AsNoTracking()
            .Where(m => m.OrgId == g.OrgId && m.Source == "idp")
            .Select(m => m.UserId).ToListAsync(ct);
        var members = new JsonArray();
        foreach (var id in memberIds)
            members.Add(new JsonObject { ["value"] = id.ToString() });
        return new JsonObject
        {
            ["schemas"] = new JsonArray(
                "urn:ietf:params:scim:schemas:core:2.0:Group"),
            ["id"] = g.Id.ToString(),
            ["displayName"] = g.IdpGroup,
            ["members"] = members,
        };
    }

    private static JsonObject ResourceType(string name, string ep) =>
        new()
        {
            ["schemas"] = new JsonArray(
                "urn:ietf:params:scim:schemas:core:2.0:ResourceType"),
            ["id"] = name,
            ["name"] = name,
            ["endpoint"] = ep,
        };

    private static JsonObject ToScimUser(UserRecord u, OrgMemberRecord m) =>
        new()
        {
            ["schemas"] = new JsonArray(
                "urn:ietf:params:scim:schemas:core:2.0:User"),
            ["id"] = u.Id.ToString(),
            ["userName"] = u.Email,
            ["displayName"] = u.DisplayName,
            ["active"] = u.DisabledAt is null,
            ["emails"] = new JsonArray(new JsonObject
            {
                ["value"] = u.Email,
                ["primary"] = true,
            }),
            ["roles"] = new JsonArray(new JsonObject
            {
                ["value"] = m.Role,
            }),
        };

    private static IResult ScimOk(JsonObject body) =>
        Results.Json(body, contentType: ScimJson);

    private static IResult ScimError(
        int status, string detail, string? scimType = null) =>
        Results.Json(new JsonObject
        {
            ["schemas"] = new JsonArray(ErrorSchema),
            ["status"] = status.ToString(),
            ["detail"] = detail,
            ["scimType"] = scimType,
        }, statusCode: status, contentType: ScimJson);
}