using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenHandsNET.Application.Sandbox;

namespace OpenHandsNET.Sandboxing;

/// <summary>
/// SPEC-35 RF-002/004: engine wiring — <c>Sandbox:Provider=local</c>
/// (Docker.DotNet in-process, default) ou <c>remote</c>
/// (<c>openhandsnet-runtime-api</c> over internal REST).
/// </summary>
public static class SandboxingRegistration
{
    /// <summary>
    /// Registers <see cref="SandboxOptions"/>, workspace/egress helpers and
    /// the selected <see cref="ISandboxEngine"/> implementation.
    /// </summary>
    public static IServiceCollection AddSandboxing(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SandboxOptions>(configuration.GetSection("Sandbox"));
        services.AddSingleton(_ => DockerClientFactory.Create());
        services.AddSingleton<WorkspaceProvisioner>();
        services.AddSingleton<DockerNetworkPolicy>();
        services.AddSingleton<SquidConfigWriter>();
        services.AddSingleton<EgressProxyService>();
        services.AddSingleton<DockerSandboxEngine>();
        services.AddHttpClient("runtime-api", (sp, c) =>
        {
            var o = sp.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<SandboxOptions>>().Value;
            if (o.RemoteBaseUrl is { } url)
            {
                c.BaseAddress = new Uri(url);
            }

            if (o.RemoteApiKey is { } key)
            {
                c.DefaultRequestHeaders.Add("X-Runtime-Key", key);
            }
        });
        services.AddSingleton<HttpSandboxEngine>();
        services.AddSingleton<ISandboxEngine>(sp =>
        {
            var o = sp.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<SandboxOptions>>().Value;
            return (o.Provider ?? "local").Equals(
                    "remote", StringComparison.OrdinalIgnoreCase)
                ? sp.GetRequiredService<HttpSandboxEngine>()
                : (ISandboxEngine)sp.GetRequiredService<DockerSandboxEngine>();
        });
        return services;
    }
}
