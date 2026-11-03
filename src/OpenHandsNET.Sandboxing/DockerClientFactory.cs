using Docker.DotNet;

namespace OpenHandsNET.Sandboxing;

/// <summary>Docker.DotNet client factory (unix socket default, DOCKER_HOST honored).</summary>
public static class DockerClientFactory
{
    private static readonly Uri WindowsDockerUri =
        new("npipe://./pipe/docker_engine");
    private static readonly Uri LinuxDockerUri =
        new("unix:///var/run/docker.sock");

    /// <summary>Creates a client from <c>DOCKER_HOST</c> or the default local socket.</summary>
    public static IDockerClient Create()
    {
        var host = Environment.GetEnvironmentVariable("DOCKER_HOST");
        Uri uri;
        if (host is null)
        {
            uri = OperatingSystem.IsWindows()
                ? WindowsDockerUri
                : LinuxDockerUri;
        }
        else
        {
            uri = new Uri(host);
        }

        return new DockerClientBuilder().WithEndpoint(uri).Build();
    }
}