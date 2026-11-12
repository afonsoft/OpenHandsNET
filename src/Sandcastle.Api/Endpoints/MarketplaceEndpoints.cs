using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Marketplace;
using Sandcastle.Domain.Identity;
using Sandcastle.Domain.Marketplace;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// SPEC-148 — <c>/api/v1/marketplace</c>: registry catalog, scoped installs,
/// toggle/remove/upgrade. Permissões: <c>perm:marketplace.install</c>
/// (browse + user scope) no grupo inteiro; escopos <c>org</c>/<c>system</c>
/// exigem adicionalmente <c>perm:marketplace.manage</c> na org alvo /
/// superadmin — validado por linha/escopo dentro do handler.
/// </summary>
public static class MarketplaceEndpoints
{
    private const string PermInstall = "perm:marketplace.install";
    private const string Tag = "Marketplace";

    /// <summary>Maps marketplace endpoints.</summary>
    public static IEndpointRouteBuilder MapMarketplaceEndpoints(
        this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/marketplace").WithTags(Tag)
            .RequireAuthorization(PermInstall);

        g.MapGet("/entries", ListEntriesAsync);
        g.MapGet("/entries/{id}", GetEntryAsync);
        g.MapGet("/installed", ListInstalledAsync);
        g.MapPost("/install", InstallAsync);
        g.MapPatch("/installed/{id:guid}", ToggleAsync);
        g.MapDelete("/installed/{id:guid}", RemoveAsync);
        g.MapPost("/upgrade/{id:guid}", UpgradeAsync);
        return app;
    }

    // -------------------------------------------------------------- catalog

    private static async Task<IResult> ListEntriesAsync(
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IMarketplaceRegistry registry,
        IMarketplaceService market,
        [FromQuery] string? q, [FromQuery] string? kind,
        [FromQuery] string? tag, CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var orgId = (await orgs.ResolveAsync(
            u, http.Request.Headers["X-Org-Id"].ToString(), ct))?.OrgId;
        IReadOnlyList<MarketplaceEntry> entries;
        IReadOnlyList<InstalledSkill> installed;
        try
        {
            entries = await registry.ListEntriesAsync(ct);
            installed = await market.ListInstalledAsync(
                u.UserId.ToString(), orgId, null, ct);
        }
        catch (MarketplaceRegistryException ex)
        {
            return RegistryProblem(http, ex);
        }

        var byEntry = installed
            .GroupBy(i => i.EntryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(),
                StringComparer.OrdinalIgnoreCase);
        var items = entries
            .Where(e => kind is null || string.Equals(
                WireKind(e.Kind), kind, StringComparison.OrdinalIgnoreCase))
            .Where(e => tag is null || e.Tags.Any(
                t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
            .Where(e => q is null
                || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (e.Description?.Contains(
                    q, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(e => ToEntryDto(e,
                byEntry.TryGetValue(e.Id, out var list) ? list : []))
            .ToList();
        return Results.Ok(new { items });
    }

    private static async Task<IResult> GetEntryAsync(
        string id, HttpContext http, ICurrentUserAccessor current,
        IMarketplaceRegistry registry, CancellationToken ct)
    {
        if (current.User is null)
        {
            return Results.Unauthorized();
        }

        MarketplaceEntry? e;
        try
        {
            e = await registry.GetEntryAsync(id, ct);
        }
        catch (MarketplaceRegistryException ex)
        {
            return RegistryProblem(http, ex);
        }

        return e is null
            ? SandcastleProblemFactory.Create(
                http, 404, "marketplace entry not found")
            : Results.Ok(ToEntryDto(e, [], detail: true));
    }

    // ------------------------------------------------------------- installed

    private static async Task<IResult> ListInstalledAsync(
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IMarketplaceService market,
        IMarketplaceRegistry registry,
        [FromQuery] string? scope, CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        if (scope is not null
            && scope is not ("user" or "org" or "system"))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "scope must be user|org|system");
        }

        var orgId = (await orgs.ResolveAsync(
            u, http.Request.Headers["X-Org-Id"].ToString(), ct))?.OrgId;
        var rows = await market.ListInstalledAsync(
            u.UserId.ToString(), orgId, ParseScopeQuery(scope), ct);
        var versions = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var e in await registry.ListEntriesAsync(ct))
            {
                versions[e.Id] = e.Version;
            }
        }
        catch (MarketplaceRegistryException)
        {
            // registry down → update_available ficam false; lista segue.
        }

        return Results.Ok(new
        {
            items = rows.Select(r => ToInstalledDto(r,
                versions.TryGetValue(r.EntryId, out var v)
                    ? v : null)).ToList(),
        });
    }

    // --------------------------------------------------------------- install

    private static async Task<IResult> InstallAsync(
        [FromBody] InstallRequest body, HttpContext http,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        IMarketplaceService market, AppDbContext db,
        CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(body.EntryId)
            || body.Scope is null)
        {
            return SandcastleProblemFactory.Create(
                http, 400, "entry_id and scope are required");
        }

        var scope = ParseScopeQuery(body.Scope);
        if (scope is null)
        {
            return SandcastleProblemFactory.Create(
                http, 400, "scope must be user|org|system");
        }

        var (scopeId, memberOfScope, scopeError) = await ResolveScopeAsync(
            scope.Value, body.ScopeId, u, orgs, db, http, ct);
        if (scopeError is not null)
        {
            return scopeError;
        }

        var result = await market.InstallAsync(
            new MarketplaceInstallRequest(
                body.EntryId.Trim(), body.Version, scope.Value, scopeId,
                u.UserId.ToString(), body.AcceptPermissions == true,
                memberOfScope, u.IsSuperAdmin), ct);
        return result.Error is { } err
            ? ErrorProblem(http, err, result)
            : Results.Created(
                $"/api/v1/marketplace/installed/{result.Installed!.Id}",
                ToInstalledDto(result.Installed, null));
    }

    /// <summary>
    /// Scope resolution + authorization: user → own id; org → manage perm
    /// na org alvo (membership+role ou superadmin); system → superadmin.
    /// </summary>
    private static async Task<(string? ScopeId, bool Member, IResult? Error)>
        ResolveScopeAsync(InstallScope scope, string? requestedScopeId,
            AuthUser u, IEffectiveOrgResolver orgs, AppDbContext db,
            HttpContext http, CancellationToken ct)
    {
        switch (scope)
        {
            case InstallScope.User:
                if (requestedScopeId is not null
                    && requestedScopeId != u.UserId.ToString())
                {
                    return (null, false, SandcastleProblemFactory.Create(
                        http, 403, "cannot install into another user's scope"));
                }

                return (u.UserId.ToString(), false, null);
            case InstallScope.Org:
                {
                    var orgId = requestedScopeId
                        ?? (await orgs.ResolveAsync(u,
                            http.Request.Headers["X-Org-Id"].ToString(),
                            ct))?.OrgId.ToString();
                    if (orgId is null || !Guid.TryParse(orgId, out var gid))
                    {
                        return (null, false, SandcastleProblemFactory.Create(
                            http, 400, "scope_id (org) is required"));
                    }

                    var role = await db.OrgMembers
                        .Where(m => m.OrgId == gid && m.UserId == u.UserId)
                        .Select(m => m.Role)
                        .FirstOrDefaultAsync(ct);
                    var member = role is not null;
                    var manage = u.IsSuperAdmin
                        || (member && RolePermissions.ForOrgRole(role)
                            .Contains(Permission.MarketplaceManage));
                    return manage
                        ? (orgId, member, null)
                        : ((string?, bool, IResult?))(null, false,
                            SandcastleProblemFactory.Create(http, 403,
                                "marketplace.manage required for org scope"));
                }
            default:
                return u.IsSuperAdmin
                    ? (null, false, null)
                    : ((string?, bool, IResult?))(null, false,
                        SandcastleProblemFactory.Create(http, 403,
                            "system scope requires super admin"));
        }
    }

    /// <summary>Row-scope gate for toggle/remove/upgrade.</summary>
    private static async Task<bool> CanManageRowAsync(
        InstalledSkill row, AuthUser u, AppDbContext db,
        CancellationToken ct) =>
        row.Scope switch
        {
            InstallScope.User =>
                row.ScopeId == u.UserId.ToString(),
            InstallScope.System =>
                u.IsSuperAdmin,
            InstallScope.Org =>
                u.IsSuperAdmin || (Guid.TryParse(row.ScopeId, out var gid)
                    && RolePermissions.ForOrgRole(
                        await db.OrgMembers
                            .Where(m => m.OrgId == gid
                                && m.UserId == u.UserId)
                            .Select(m => m.Role)
                            .FirstOrDefaultAsync(ct))
                        .Contains(Permission.MarketplaceManage)),
            _ => false,
        };

    // --------------------------------------------------------------- mutate

    private static async Task<IResult> ToggleAsync(
        Guid id, [FromBody] ToggleRequest body, HttpContext http,
        ICurrentUserAccessor current, IMarketplaceService market,
        AppDbContext db, CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var row = await market.GetInstalledAsync(id, ct);
        if (row is null)
        {
            return SandcastleProblemFactory.Create(
                http, 404, "installed entry not found");
        }

        if (!await CanManageRowAsync(row, u, db, ct))
        {
            return SandcastleProblemFactory.Create(
                http, 403, "cannot manage this install scope");
        }

        var (updated, error) = await market.SetEnabledAsync(
            id, body.Enabled, ct);
        return updated is { } r
            ? Results.Ok(ToInstalledDto(r, null))
            : SandcastleProblemFactory.Create(
                http, 404, error ?? "installed entry not found");
    }

    private static async Task<IResult> RemoveAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IMarketplaceService market, AppDbContext db,
        CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var row = await market.GetInstalledAsync(id, ct);
        if (row is null)
        {
            return SandcastleProblemFactory.Create(
                http, 404, "installed entry not found");
        }

        if (!await CanManageRowAsync(row, u, db, ct))
        {
            return SandcastleProblemFactory.Create(
                http, 403, "cannot manage this install scope");
        }

        return await market.RemoveAsync(id, ct)
            ? Results.NoContent()
            : SandcastleProblemFactory.Create(
                http, 404, "installed entry not found");
    }

    private static async Task<IResult> UpgradeAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IMarketplaceService market, AppDbContext db,
        CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var row = await market.GetInstalledAsync(id, ct);
        if (row is null)
        {
            return SandcastleProblemFactory.Create(
                http, 404, "installed entry not found");
        }

        if (!await CanManageRowAsync(row, u, db, ct))
        {
            return SandcastleProblemFactory.Create(
                http, 403, "cannot manage this install scope");
        }

        var (updated, error) = await market.UpgradeAsync(id, ct);
        return updated is { } r
            ? Results.Ok(ToInstalledDto(r, row.Version))
            : ErrorProblem(http, error ?? "upgrade failed", null);
    }

    // --------------------------------------------------------------- dto

    private static object ToEntryDto(
        MarketplaceEntry e, IReadOnlyList<InstalledSkill> installs,
        bool detail = false) => new
        {
            id = e.Id,
            name = e.Name,
            version = e.Version,
            kind = WireKind(e.Kind),
            description = e.Description,
            author = e.Author,
            tags = e.Tags,
            min_app_version = e.MinAppVersion,
            source = e.Source,
            permissions_required = e.PermissionsRequired,
            changelog_url = e.ChangelogUrl,
            readme = detail ? e.Readme : null,
            files = detail
            ? e.Files.Select(f => (object)new
            {
                path = f.Path,
                remote = f.Url is not null,
            }).ToList()
            : null,
            entry_ids = e.EntryIds.Count > 0 ? e.EntryIds : null,
            installed = installs.Select(i => (object)new
            {
                installed_id = i.Id,
                scope = WireScope(i.Scope),
                scope_id = i.ScopeId,
                i.Version,
                i.Enabled,
            }).ToList(),
            update_available = installs.Any(
            i => i.Version != e.Version),
        };

    private static object ToInstalledDto(
        InstalledSkill r, string? latestVersion) => new
        {
            id = r.Id,
            entry_id = r.EntryId,
            scope = WireScope(r.Scope),
            scope_id = r.ScopeId,
            version = r.Version,
            enabled = r.Enabled,
            installed_by = r.InstalledBy,
            installed_at = r.InstalledAt,
            update_available = latestVersion is not null
            && latestVersion != r.Version,
        };

    private static string WireKind(MarketplaceEntryKind k) => k switch
    {
        MarketplaceEntryKind.McpServer => "mcp-server",
        MarketplaceEntryKind.Agent => "agent",
        MarketplaceEntryKind.Hook => "hook",
        MarketplaceEntryKind.Bundle => "bundle",
        _ => "skill",
    };

    private static string WireScope(InstallScope s) => s switch
    {
        InstallScope.User => "user",
        InstallScope.Org => "org",
        _ => "system",
    };

    private static InstallScope? ParseScopeQuery(string? scope) =>
        scope?.ToLowerInvariant() switch
        {
            "user" => InstallScope.User,
            "org" => InstallScope.Org,
            "system" => InstallScope.System,
            _ => null,
        };

    private static IResult ErrorProblem(
        HttpContext http, string code, MarketplaceInstallResult? r) =>
        code switch
        {
            "entry_not_found" => SandcastleProblemFactory.Create(
                http, 404, "marketplace entry not found"),
            "version_mismatch" => SandcastleProblemFactory.Create(
                http, 422, "requested version not in registry"),
            "min_app_version" => SandcastleProblemFactory.Create(
                http, 422, "entry requires a newer Sandcastle version"),
            "permissions_required" => SandcastleProblemFactory.Create(
                http, 422,
                "entry declares permissions_required — resend with accept_permissions=true"),
            "scope_forbidden" => SandcastleProblemFactory.Create(
                http, 403, "scope not permitted for caller"),
            "already_installed" => SandcastleProblemFactory.Create(
                http, 409, "entry already installed in this scope"),
            "already_latest" => SandcastleProblemFactory.Create(
                http, 409, "installed version is already latest"),
            "checksum_mismatch" or "checksum_required"
                or "invalid_manifest" or "bundle_too_deep"
                or "builtin_override" =>
                SandcastleProblemFactory.Create(http, 422,
                    $"manifest validation failed: {code}"),
            "registry_unavailable" or "registry_forbidden" =>
                SandcastleProblemFactory.Create(
                    http, 502, $"registry fetch failed: {code}"),
            _ => SandcastleProblemFactory.Create(
                http, 500, $"marketplace install failed: {code}"),
        };

    private static IResult RegistryProblem(
        HttpContext http, MarketplaceRegistryException ex) =>
        ex.Code is "registry_unavailable" or "registry_forbidden"
            ? SandcastleProblemFactory.Create(
                http, 502, $"registry fetch failed: {ex.Code}")
            : SandcastleProblemFactory.Create(
                http, 422, $"registry manifest error: {ex.Code}");

    /// <summary>POST /install body.</summary>
    public sealed record InstallRequest(
        string? EntryId, string? Version, string? Scope,
        string? ScopeId, bool? AcceptPermissions);

    /// <summary>PATCH /installed/{id} body.</summary>
    public sealed record ToggleRequest(bool Enabled);
}