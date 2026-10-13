using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Identity;

namespace OpenHandsNET.Api.Auth;

/// <summary>
/// Bearer <c>sk-*</c> authentication (SPEC-08 RF-002). Validates the key via
/// <see cref="IApiKeyService"/> and emits a principal with user_id, auth_type
/// and bound_org_id claims.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>Scheme name used in <c>AuthenticationSchemes</c>.</summary>
    public const string SchemeName = "apikey";

    /// <summary>Claim carrying the bound_org_id of the key (empty = unbound).</summary>
    public const string BoundOrgClaim = "bound_org_id";

    /// <summary>Claim carrying the API key id.</summary>
    public const string KeyIdClaim = "api_key_id";

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var token = header["Bearer ".Length..].Trim();
        if (!token.StartsWith("sk-", StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        var keys = Context.RequestServices.GetRequiredService<IApiKeyService>();
        var resolution = await keys.ValidateAsync(token, Context.RequestAborted);
        if (resolution is null)
            return AuthenticateResult.Fail("invalid or expired api key");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, resolution.UserId.ToString()),
            new("auth_type", "bearer"),
            new(KeyIdClaim, resolution.KeyId.ToString()),
        };
        if (resolution.BoundOrgId is { } bound)
            claims.Add(new Claim(BoundOrgClaim, bound.ToString()));

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, SchemeName));
    }
}
