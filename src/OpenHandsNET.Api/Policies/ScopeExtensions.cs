using OpenHandsNET.Application.Identity;

namespace OpenHandsNET.Api.Policies;

/// <summary>
/// <c>.RequireScope("conversations:write")</c> — declara o escopo de API
/// exigido por um endpoint (SPEC-57 RF-002). Compõe com as políticas
/// <c>perm:*</c> já existentes (ambas devem passar).
/// </summary>
public static class ScopeExtensions
{
    /// <summary>Declara escopo exigido no endpoint.</summary>
    public static TBuilder RequireScope<TBuilder>(
        this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization($"scope:{scope}");

    /// <summary>
    /// Checagem imperativa dentro de handlers (ex.: impersonation).
    /// Cookie/<c>sk-*</c> sem escopos passa; <c>svc_*</c> precisa do escopo.
    /// </summary>
    public static bool HasScope(this AuthUser? user, string scope) =>
        user?.Scopes is not { Count: > 0 } scopes
        || scopes.Contains(scope);
}
