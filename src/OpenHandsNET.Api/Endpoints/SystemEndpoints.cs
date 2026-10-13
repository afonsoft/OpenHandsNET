using System.Reflection;
using Microsoft.Extensions.Options;
using OpenHandsNET.Api.Options;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// System endpoints: liveness/readiness and public runtime config.
/// </summary>
public static class SystemEndpoints
{
    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0-dev";

    /// <summary>Maps <c>/api/v1/config</c>.</summary>
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/config", (IOptions<OpenHandsNetOptions> options) => Results.Ok(new
        {
            version = Version,
            deploymentMode = options.Value.DeploymentMode,
            authProvider = options.Value.Auth.Provider,
            features = new { multiTenancy = options.Value.DeploymentMode != "oss" },
        }))
            .WithTags("System")
            .AllowAnonymous();

        return app;
    }
}
