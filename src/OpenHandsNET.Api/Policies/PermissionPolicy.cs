using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Identity;

namespace OpenHandsNET.Api.Policies;

/// <summary>Authorization requirement carrying a <see cref="Permission"/>.</summary>
public sealed class PermissionRequirement(Permission permission)
    : IAuthorizationRequirement
{
    /// <summary>Required permission.</summary>
    public Permission Permission { get; } = permission;
}

/// <summary>
/// Dynamic policy provider: policies named <c>perm:{wire_name}</c> map to a
/// <see cref="PermissionRequirement"/> — e.g. <c>perm:manage_api_keys</c>.
/// </summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    : DefaultAuthorizationPolicyProvider(options)
{
    /// <inheritdoc />
    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith("perm:", StringComparison.Ordinal)
            && PermissionExtensions.Parse(policyName[5..]) is { } permission)
        {
            var policy = new AuthorizationPolicyBuilder()
                .AddRequirements(new PermissionRequirement(permission))
                // Accept both cookie sessions and Bearer sk-* principals.
                .AddAuthenticationSchemes("cookie", Api.Auth.ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }
        if (policyName.StartsWith("scope:", StringComparison.Ordinal))
        {
            var policy = new AuthorizationPolicyBuilder()
                .AddRequirements(new ScopeRequirement(policyName[6..]))
                .AddAuthenticationSchemes("cookie", Api.Auth.ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }
        return base.GetPolicyAsync(policyName);
    }
}

/// <summary>Marker requirement for financial-data access (SPEC-08 RF-005).</summary>
public sealed class FinancialDataRequirement : IAuthorizationRequirement;

/// <summary>Endpoint-level scope (SPEC-57 RF-002).</summary>
public sealed class ScopeRequirement(string scope)
    : IAuthorizationRequirement
{
    /// <summary>Required scope wire name (e.g. conversations:write).</summary>
    public string Scope { get; } = scope;
}

/// <summary>
/// Scope check (SPEC-57 RF-002): cookie e <c>sk-*</c> sem ScopesJson
/// passam (acesso pleno do papel); <c>svc_*</c> precisa do escopo
/// declarado. 403 genérico — não vaza escopos. Audit
/// <c>auth.scope_denied</c> best-effort.
/// </summary>
public sealed class ScopeHandler(IServiceScopeFactory scopeFactory,
    IHttpContextAccessor http) : AuthorizationHandler<ScopeRequirement>
{
    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        var ctx = http.HttpContext;
        if (ctx is null)
            return;
        var accessor = ctx.RequestServices
            .GetRequiredService<ICurrentUserAccessor>();
        var user = accessor.User;
        if (user is null)
            return;

        // Sem restrição de escopo: cookie ou sk-* legada.
        if (user.Scopes is not { Count: > 0 } scopes
            || scopes.Contains(requirement.Scope))
        {
            context.Succeed(requirement);
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var audit = scope.ServiceProvider
                .GetRequiredService<IAuditService>();
            await audit.RecordAsync(
                "auth.scope_denied", user.UserId.ToString(),
                Guid.TryParse(
                    ctx.Request.RouteValues["org_id"]?.ToString(),
                    out var orgId) ? orgId : null,
                "endpoint",
                ctx.Request.Path.Value,
                cancellationToken: ctx.RequestAborted);
        }
        catch (Exception)
        {
            // audit best-effort — não bloqueia o deny.
        }
    }
}

/// <summary>
/// Permission check (SPEC-08 RF-004), exact upstream order:
/// 1. resolve target org — <c>{org_id}</c> route &gt; X-Org-Id &gt; effective org;
/// 2. API-key binding check — key bound to org B hitting org A → 403;
/// 3. suspended org blocks org-role evaluation (super-role still allowed);
/// 4. org-role permissions → success;
/// 5. super-role fallback → success;
/// 6. otherwise fail ("Missing required permission: {perm}").
/// </summary>
public sealed class PermissionHandler(
    IServiceScopeFactory scopeFactory,
    IHttpContextAccessor http) : AuthorizationHandler<PermissionRequirement>
{
    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var ctx = http.HttpContext;
        if (ctx is null)
            return;

        var accessor = ctx.RequestServices.GetRequiredService<ICurrentUserAccessor>();
        var user = accessor.User;
        if (user is null)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.AppDbContext>();

        // 1. target org resolution
        var targetOrg = await ResolveTargetOrgAsync(ctx, scope, user);

        // 2. binding check — bound key can't cross to a different org
        if (targetOrg is { } orgId && user.BoundOrgId is { } bound && bound != orgId)
            return;

        // 3-4. org-role evaluation — service users carregam a role
        // no próprio registro (SPEC-57); humanos via OrgMembers.
        if (targetOrg is { } oid
            && await OrgRolePermitsAsync(db, oid, user, requirement.Permission))
        {
            context.Succeed(requirement);
            return;
        }

        // 5. super-role fallback — explicit perms, no org inheritance
        if (user.IsSuperAdmin
            && RolePermissions.ForSuperRole(RoleName.SuperAdmin)
                .Contains(requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }

    private static async Task<Guid?> ResolveTargetOrgAsync(
        HttpContext ctx, AsyncServiceScope scope, AuthUser user)
    {
        if (ctx.Request.RouteValues["org_id"] is { } routeOrg
            && Guid.TryParse(routeOrg.ToString(), out var rg))
        {
            return rg;
        }
        if (ctx.Request.Headers.TryGetValue("X-Org-Id", out var headerOrg)
            && Guid.TryParse(headerOrg.ToString(), out var hg))
        {
            return hg;
        }
        var resolver = scope.ServiceProvider
            .GetRequiredService<IEffectiveOrgResolver>();
        return (await resolver.ResolveAsync(
            user, null, ctx.RequestAborted))?.OrgId;
    }

    private static async Task<bool> OrgRolePermitsAsync(
        Infrastructure.Persistence.AppDbContext db, Guid oid,
        AuthUser user, Permission permission)
    {
        var org = await db.Organizations.FindAsync([oid]);
        if (org?.SuspendedAt is not null)
        {
            return false;
        }
        var role = user.ActorKind == ActorKind.ServiceUser
            ? user.Role
            : await db.OrgMembers
                .Where(m => m.OrgId == oid && m.UserId == user.UserId)
                .Select(m => m.Role)
                .FirstOrDefaultAsync();
        return RolePermissions.ForOrgRole(role).Contains(permission);
    }
}