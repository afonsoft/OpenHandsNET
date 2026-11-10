using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

using Sandcastle.Api.Auth;
using Sandcastle.Application.Identity;
using Sandcastle.Infrastructure.Identity;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// Auth endpoints (SPEC-08 §5): OIDC broker login/callback, dev-login for
/// <c>Auth:Provider=none</c>, session info, TOS/onboarding, logout.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>Cookie scheme name (chunked via ChunkingCookieManager).</summary>
    public const string CookieScheme = "cookie";

    /// <summary>Map auth + device-flow endpoints.</summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var oauth = app.MapGroup("/oauth");

        // OIDC broker login — Challenge redirects to the configured authority.
        // SPEC-97 RF-002: provider != oidc → 404 (o scheme 'oidc' não é
        // registrado nesse modo — Challenge lançava InvalidOperation → 500).
        oauth.MapGet("/oidc/login", (
            string? returnTo, IConfiguration config) =>
            OidcLogin(returnTo, config)).AllowAnonymous();

        // Silent refresh parity — re-issues session without rewriting the
        // principal cookie upstream-style.
        oauth.MapGet("/oidc/offline/callback", (HttpContext ctx) =>
            Results.Ok(new { refreshed = ctx.User.Identity?.IsAuthenticated == true }))
            .RequireAuthorization();

        // RFC 8628 device flow (RF-009).
        oauth.MapPost("/device/authorize", async (
            HttpContext ctx, IDeviceFlowService devices, CancellationToken ct) =>
        {
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var auth = await devices.StartAsync(baseUrl, ct);
            return Results.Ok(new
            {
                device_code = auth.DeviceCode,
                user_code = auth.UserCode,
                verification_uri = auth.VerificationUri,
                verification_uri_complete = auth.VerificationUriComplete,
                expires_in = auth.ExpiresIn,
                interval = auth.Interval,
            });
        }).AllowAnonymous();

        oauth.MapPost("/device/token",
            DeviceTokenAsync).WithTags(EndpointTags.Api)
            .AllowAnonymous().DisableAntiforgery();

        oauth.MapGet("/device/verify", (string? user_code) =>
            Results.Content(
                $"""
                <!doctype html><html><body>
                <h1>Authorize device</h1>
                <form method="post" action="/oauth/device/verify-authenticated">
                  <input name="user_code" value="{user_code}" />
                  <button name="decision" value="approve">Approve</button>
                  <button name="decision" value="deny">Deny</button>
                </form></body></html>
                """, "text/html")).RequireAuthorization();

        oauth.MapPost("/device/verify-authenticated", (
            [FromForm] string? user_code, [FromForm] string? decision,
            ICurrentUserAccessor current, IDeviceFlowService devices,
            CancellationToken ct) =>
            DeviceVerifyAuthenticatedAsync(user_code, decision, current, devices, ct))
            .RequireAuthorization().DisableAntiforgery();

        var auth = app.MapGroup("/api/v1/auth");

        // SPEC-132: probe de sessão anônimo — o client decide entre
        // boot direto (provider=none) ou a tela /auth. Nunca vaza
        // dados sensíveis: só provider + perfil público da sessão.
        auth.MapGet("/session", (
                HttpContext ctx, ICurrentUserAccessor current,
                IConfiguration config) =>
            {
                var user = current.User;
                return Results.Ok(new
                {
                    provider = config["Auth:Provider"] ?? "none",
                    authenticated = user is not null,
                    user = user is null ? null : new
                    {
                        id = user.UserId,
                        email = user.Email,
                        display_name = user.DisplayName,
                        is_super_admin = user.IsSuperAdmin,
                    },
                });
            }).AllowAnonymous().WithTags(EndpointTags.Api)
            .Produces<object>()
            .WithSummary("Current auth session + provider");

        // OSS local login — Auth:Provider=none (first user bootstrap) ou
        // 'local' (login confiado por e-mail, sem senha — deployment
        // single-tenant de confiança, paridade upstream).
        auth.MapPost("/dev-login", (
            DevLoginRequest req, HttpContext ctx,
            IUserService users, IConfiguration config, IAuditService audit,
            CancellationToken ct) =>
            DevLoginAsync(req, ctx, users, config, audit, ct)).AllowAnonymous();

        auth.MapPost("/accept_tos", async (
            ICurrentUserAccessor current, IUserService users, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            await users.AcceptTosAsync(user.UserId, ct);
            return Results.Ok(new { accepted = true });
        }).RequireAuthorization();

        auth.MapGet("/onboarding_status", async (
            ICurrentUserAccessor current, IUserService users, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            var status = await users.OnboardingStatusAsync(user.UserId, ct);
            return Results.Ok(new
            {
                needs_onboarding = status?.NeedsOnboarding ?? false,
                tos_accepted = status?.TosAccepted ?? false,
            });
        }).RequireAuthorization();

        auth.MapPost("/logout", async (HttpContext ctx, IAuditService audit,
            ICurrentUserAccessor current) =>
        {
            var uid = current.User?.UserId.ToString();
            await ctx.SignOutAsync(CookieScheme);
            await audit.RecordAsync("auth.logout", uid);
            return Results.Ok(new { logged_out = true });
        }).RequireAuthorization();

        // Paridade /api/authenticate: user + orgs + idp_type.
        app.MapPost("/api/v1/authenticate",
            AuthenticateAsync).WithTags(EndpointTags.Api)
            .RequireAuthorization();

        return app;
    }


    private static IResult OidcLogin(
        string? returnTo, IConfiguration config) =>
        !string.Equals(config["Auth:Provider"], "oidc",
            StringComparison.OrdinalIgnoreCase)
            ? Results.NotFound()
            : Results.Challenge(
                new AuthenticationProperties
                {
                    RedirectUri = returnTo ?? "/",
                },
                ["oidc"]);

    private static async Task<IResult> DeviceVerifyAuthenticatedAsync(
        [FromForm] string? user_code, [FromForm] string? decision,
        ICurrentUserAccessor current, IDeviceFlowService devices,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null || string.IsNullOrWhiteSpace(user_code))
            return Results.BadRequest(new { error = "invalid_request" });
        var ok = await devices.VerifyAsync(
            user, user_code, decision != "deny", ct);
        return ok
            ? Results.Content("<html><body>You may close this window.</body></html>",
                "text/html")
            : Results.BadRequest(new { error = "invalid_user_code" });
    }

    private static async Task<IResult> DevLoginAsync(
        DevLoginRequest req, HttpContext ctx,
        IUserService users, IConfiguration config, IAuditService audit,
        CancellationToken ct)
    {
        var provider = config["Auth:Provider"];
        if (!string.Equals(provider, "none", StringComparison.Ordinal)
            && !string.Equals(provider, "local", StringComparison.Ordinal))
            return Results.NotFound();
        if (string.IsNullOrWhiteSpace(req.Email))
            return Results.BadRequest(new { error = "email_required" });
        var user = await users.GetOrCreateAsync(
            req.Email, req.DisplayName, "local",
            multiTenancyEnabled: config.GetValue("MultiTenancy:Enabled", false),
            cancellationToken: ct);
        await SignInCookieAsync(ctx, user);
        await audit.RecordAsync(
            "auth.login", user.UserId.ToString(), null, "user",
            user.UserId.ToString(), ctx.Connection.RemoteIpAddress?.ToString(),
            ctx.Request.Headers.UserAgent.ToString(),
            metadata: new { provider = "dev" }, cancellationToken: ct);
        return Results.Ok(new { user_id = user.UserId, email = user.Email });
    }

    private static async Task<IResult> DeviceTokenAsync(
        [FromForm] string? device_code,
        [FromForm] string? grant_type,
        IDeviceFlowService devices, CancellationToken ct)
    {
        // RFC 8628: grant_type é obrigatório e deve ser device_code.
        if (!string.Equals(grant_type,
                "urn:ietf:params:oauth:grant-type:device_code",
                StringComparison.Ordinal))
            return Results.BadRequest(new { error = "unsupported_grant_type" });
        if (string.IsNullOrWhiteSpace(device_code))
            return Results.BadRequest(new { error = "invalid_device_code" });
        var result = await devices.PollAsync(device_code, ct);
        return result.Status switch
        {
            DeviceTokenStatus.Approved => Results.Ok(new
            {
                access_token = result.AccessToken,
                token_type = "bearer",
            }),
            DeviceTokenStatus.Pending => Results.BadRequest(
                new { error = "authorization_pending" }),
            DeviceTokenStatus.Denied => Results.BadRequest(
                new { error = "access_denied" }),
            DeviceTokenStatus.Expired => Results.BadRequest(
                new { error = "expired_token" }),
            _ => Results.BadRequest(new { error = "invalid_device_code" }),
        };
    }

    private static async Task<IResult> AuthenticateAsync(
        HttpContext ctx, ICurrentUserAccessor current, IOrgService orgs,
        IIdpSyncService idpSync, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        // SPEC-58 RF-002: aplica grupos do IdP (claims "groups"/"role")
        // às memberships — idempotente; revoga memberships idp-sourced
        // cujo mapping deixou de casar.
        var groupClaims = ctx.User.FindAll("groups")
            .Concat(ctx.User.FindAll("group"))
            .Concat(ctx.User.FindAll(
                "http://schemas.microsoft.com/ws/2008/06/identity/claims/groups"))
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        try
        {
            await idpSync.ApplyOnLoginAsync(user.UserId, groupClaims, ct);
        }
        catch (Exception)
        {
            // sync é best-effort no login — não bloqueia autenticação.
        }
        var mine = await orgs.ListMineAsync(user, ct);
        return Results.Ok(new
        {
            user = new
            {
                id = user.UserId,
                email = user.Email,
                display_name = user.DisplayName,
                current_org_id = user.CurrentOrgId,
                is_super_admin = user.IsSuperAdmin,
            },
            orgs = mine.Select(o => new { o.Id, o.Name, o.Slug, o.Role }),
            idp_type = user.IdpType,
            auth_type = user.AuthType.ToString().ToLowerInvariant(),
        });
    }

    /// <summary>Issue the chunked cookie session for a resolved user.</summary>
    internal static async Task SignInCookieAsync(HttpContext ctx, AuthUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Email, user.Email),
            new("auth_type", "cookie"),
        };
        if (user.IdpType is not null)
            claims.Add(new Claim("idp_type", user.IdpType));
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieScheme));
        await ctx.SignInAsync(
            CookieScheme, principal,
            new AuthenticationProperties { IsPersistent = true });
    }

    /// <summary>Dev-login body.</summary>
    public sealed record DevLoginRequest(string Email, string? DisplayName);
}