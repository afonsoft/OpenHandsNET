using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using OpenHandsNET.Domain.Identity;

namespace OpenHandsNET.Api.Policies;

/// <summary>
/// Emits the upstream-parity body <c>403 Missing required permission: {perm}</c>
/// when a <see cref="PermissionRequirement"/> fails (SPEC-08 RF-004).
/// </summary>
public sealed class PermissionResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    /// <inheritdoc />
    public async Task HandleAsync(
        RequestDelegate next, HttpContext context,
        AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && policy.Requirements.OfType<PermissionRequirement>()
                .FirstOrDefault() is { } req)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                detail = $"Missing required permission: {req.Permission.ToWireName()}",
            }, context.RequestAborted);
            return;
        }
        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
