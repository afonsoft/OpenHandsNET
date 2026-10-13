using System.Security.Claims;
using OpenHandsNET.Application.Identity;

namespace OpenHandsNET.Api.Auth;

/// <summary>
/// Resolves the authenticated principal into an <see cref="AuthUser"/> once
/// per request (cached). Cookie sessions carry user_id + idp_type claims;
/// the apikey scheme carries user_id + bound_org_id.
/// </summary>
public sealed class HttpCurrentUserAccessor(
    IHttpContextAccessor http,
    IUserService users) : ICurrentUserAccessor
{
    private AuthUser? _resolved;
    private bool _attempted;

    /// <inheritdoc />
    public AuthUser? User
    {
        get
        {
            if (!_attempted)
            {
                _attempted = true;
                _resolved = ResolveAsync().GetAwaiter().GetResult();
            }
            return _resolved;
        }
    }

    /// <inheritdoc />
    public Guid RequireUserId() =>
        User?.UserId ?? throw new UnauthorizedAccessException("authentication required");

    private async Task<AuthUser?> ResolveAsync()
    {
        var principal = http.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated is not true)
            return null;
        var idClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(idClaim, out var userId))
            return null;

        var user = await users.GetAsync(userId);
        if (user is null)
            return null;

        var authType = principal.HasClaim("auth_type", "bearer")
            ? AuthType.Bearer : AuthType.Cookie;
        var bound = Guid.TryParse(
            principal.FindFirst(ApiKeyAuthenticationHandler.BoundOrgClaim)?.Value,
            out var b) ? b : (Guid?)null;
        return user with { AuthType = authType, BoundOrgId = bound };
    }
}
