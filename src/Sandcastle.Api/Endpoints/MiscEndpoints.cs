namespace Sandcastle.Api.Endpoints;

/// <summary>
/// Misc endpoints: <c>/user</c> (local-mode user stub).
/// Mensagens durante run passam por <c>/app-conversations/{id}/send-message</c>
/// → <c>MessageEvent</c> → canal do runner (SPEC-137).
/// </summary>
public static class MiscEndpoints
{
    /// <summary>Maps misc endpoints.</summary>
    public static IEndpointRouteBuilder MapMiscEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/user", () => Results.Ok(new
        {
            user_id = "local",
            name = "Sandcastle Local User",
            deployment_mode = "oss",
        })).WithTags("User").AllowAnonymous();

        return app;
    }
}