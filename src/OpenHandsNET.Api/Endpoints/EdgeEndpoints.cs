using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Application.Identity;
using OpenHandsNET.Application.Security;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// SPEC-59 RF-001/004: CRUD de IP access rules (global + por-org) e o
/// simulador <c>/edge/check</c>.
/// </summary>
public static class EdgeEndpoints
{
    public static IEndpointRouteBuilder MapEdgeEndpoints(
        this IEndpointRouteBuilder app)
    {
        // Global rules — instance admin.
        var g = app.MapGroup("/api/v1/ip-rules")
            .RequireAuthorization("perm:manage_super_admins");
        g.MapGet("", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(new { rules = await ListAsync(db, "global", ct) }));
        g.MapPost("", async (HttpContext ctx, AppDbContext db,
            ICurrentUserAccessor current, IAuditService audit,
            CancellationToken ct) =>
            await CreateAsync(ctx, db, current, audit, "global", ct));
        g.MapDelete("/{id:guid}", async (
            Guid id, AppDbContext db, CancellationToken ct) =>
            await DeleteAsync(db, "global", id, ct));

        // Org-scoped rules.
        var o = app.MapGroup("/api/v1/orgs/{org_id:guid}/ip-rules")
            .RequireAuthorization("perm:edit_org_settings");
        o.MapGet("", async (Guid org_id, AppDbContext db,
            CancellationToken ct) =>
            Results.Ok(new
            {
                rules = await ListAsync(db, $"org:{org_id}", ct),
            }));
        o.MapPost("", async (Guid org_id, HttpContext ctx,
            AppDbContext db, ICurrentUserAccessor current,
            IAuditService audit, CancellationToken ct) =>
            await CreateAsync(ctx, db, current, audit,
                $"org:{org_id}", ct));
        o.MapDelete("/{id:guid}", async (Guid org_id, Guid id,
            AppDbContext db, CancellationToken ct) =>
            await DeleteAsync(db, $"org:{org_id}", id, ct));

        // Simulador: {ip?, message?} → decisão.
        app.MapPost("/api/v1/edge/check", async (
            HttpContext ctx, AppDbContext db, IInputGuard guard,
            IEffectiveOrgResolver orgs, ICurrentUserAccessor current,
            CancellationToken ct) =>
        {
            JsonNode? body;
            try
            {
                body = await JsonNode.ParseAsync(
                    ctx.Request.Body, cancellationToken: ct);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "invalid_json" });
            }
            var ipStr = StringField(body, "ip");
            var message = StringField(body, "message");
            var result = new JsonObject();
            if (!string.IsNullOrEmpty(ipStr)
                && System.Net.IPAddress.TryParse(ipStr, out var ip))
            {
                var rules = await db.IpAccessRules.AsNoTracking()
                    .ToListAsync(ct);
                var denies = rules.Where(r => r.Action == "deny").ToList();
                var allows = rules.Where(r => r.Action == "allow").ToList();
                var blocked = denies.Any(r =>
                        Middleware.EdgeAccessMiddleware.Matches(r.Cidr, ip))
                    || (allows.Count > 0 && !allows.Any(r =>
                        Middleware.EdgeAccessMiddleware.Matches(r.Cidr, ip)));
                result["ip"] = blocked ? "block" : "allow";
            }
            if (!string.IsNullOrEmpty(message))
            {
                var org = current.User is { } u
                    ? await orgs.ResolveAsync(
                        u, ctx.Request.Headers["X-Org-Id"].ToString(), ct)
                    : null;
                var g = await guard.ScoreAsync(
                    message, "ui", org?.OrgId, ct);
                result["guard"] = new JsonObject
                {
                    ["verdict"] = g.Verdict.ToString().ToLowerInvariant(),
                    ["categories"] = new JsonArray(
                        g.Categories.Select(c => (JsonNode)c).ToArray()),
                };
            }
            return Results.Ok(result);
        }).RequireAuthorization();

        return app;
    }

    private static async Task<object[]> ListAsync(
        AppDbContext db, string scope, CancellationToken ct) =>
        await db.IpAccessRules.AsNoTracking()
            .Where(r => r.Scope == scope)
            .OrderByDescending(r => r.Priority)
            .Select(r => (object)new
            {
                r.Id,
                r.Scope,
                r.Cidr,
                r.Action,
                r.Priority,
            })
            .ToArrayAsync(ct);

    /// <summary>String field tolerant to wrong JSON kinds (fuzz 500s).</summary>
    private static string? StringField(JsonNode? body, string key) =>
        body?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int IntField(JsonNode? body, string key, int fallback) =>
        body?[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : fallback;

    private static async Task<IResult> CreateAsync(
        HttpContext ctx, AppDbContext db, ICurrentUserAccessor current,
        IAuditService audit, string scope, CancellationToken ct)
    {
        JsonNode? body;
        try
        {
            body = await JsonNode.ParseAsync(
                ctx.Request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "invalid_json" });
        }
        var cidr = StringField(body, "cidr");
        var action = StringField(body, "action") ?? "deny";
        var priority = IntField(body, "priority", 0);
        var cidrOk = !string.IsNullOrWhiteSpace(cidr)
            && System.Net.IPAddress.TryParse(cidr.Split('/')[0], out _)
            && (!cidr.Contains('/')
                || int.TryParse(cidr.Split('/')[1], out var bits)
                    && bits >= 0 && bits <= 128);
        if (!cidrOk || action is not ("allow" or "deny"))
        {
            return Results.BadRequest(new { error = "invalid_cidr" });
        }
        var rec = new IpAccessRuleRecord
        {
            Id = Guid.NewGuid(),
            Scope = scope,
            Cidr = cidr!,
            Action = action,
            Priority = priority,
            CreatedBy = current.User?.UserId ?? Guid.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.IpAccessRules.Add(rec);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(
            "edge.ip_rule_created",
            current.User?.UserId.ToString(), null, "ip_rule",
            rec.Id.ToString(), null, null,
            metadata: new { scope, cidr, action },
            cancellationToken: ctx.RequestAborted);
        return Results.Ok(new
        {
            rec.Id,
            rec.Scope,
            rec.Cidr,
            rec.Action,
            rec.Priority,
        });
    }

    private static async Task<IResult> DeleteAsync(
        AppDbContext db, string scope, Guid id, CancellationToken ct) =>
        await db.IpAccessRules
            .Where(r => r.Id == id && r.Scope == scope)
            .ExecuteDeleteAsync(ct) > 0
            ? Results.NoContent() : Results.NotFound();
}