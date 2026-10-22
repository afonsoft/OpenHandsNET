using Docker.DotNet;

namespace OpenHandsNET.Sandboxing;

/// <summary>Docker.DotNet client factory (unix socket default, DOCKER_HOST honored).</summary>
public static class DockerClientFactory
{
    /// <summary>Creates a client from <c>DOCKER_HOST</c> or the default local socket.</summary>
    public static IDockerClient Create()
    {
        var host = Environment.GetEnvironmentVariable("DOCKER_HOST");
        Uri uri;
        if (host is null)
        {
            uri = OperatingSystem.IsWindows()
                ? new Uri("npipe://./pipe/docker_engine")
                : new Uri("unix:///var/run/docker.sock");
        }
        else
        {
            uri = new Uri(host);
        }

        return new DockerClientBuilder().WithEndpoint(uri).Build();
    }
}