using Docker.DotNet;
using Docker.DotNet.Models;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Sandbox;

namespace OpenHandsNET.Sandboxing;

/// <summary>
/// SPEC-35: <see cref="ISandboxEngine"/> backed by the local Docker engine.
/// Owns every daemon-local concern: image pull, container create/start,
/// egress network+proxy, DinD sidecar, workspace bind prep, agent port
/// publishing and the stateless warm pool (claim = rename
/// <c>ohn-pool-*</c> → <c>ohn-sandbox-*</c>).
/// Hosted by the Api (<c>Sandbox:Provider=local</c>) and by
/// <c>openhandsnet-runtime-api</c>.
/// </summary>
public sealed class DockerSandboxEngine(
    IDockerClient docker,
    WorkspaceProvisioner workspaces,
    DockerNetworkPolicy egress,
    IOptions<SandboxOptions> options,
    ILogger<DockerSandboxEngine> logger,
    EgressProxyService? egressProxy = null) : ISandboxEngine
{
    /// <inheritdoc />
    public bool IsRemote => false;

    /// <inheritdoc />
    public bool SupportsPause => true;

    /// <inheritdoc />
    public async Task<EngineSandboxInfo> CreateAndStartAsync(
        EngineCreateRequest request, CancellationToken ct)
    {
        await EnsureImageAsync(request.Image, ct);

        var env = new List<string>(request.Env);
        env.AddRange(egress.EgressEnv());

        NetworkingConfig? networking = null;
        if (egress.IsAllowlist)
        {
            // SPEC-19 fail-closed: allowlist exige proxy efetivo.
            var net = await egress.EnsureNetworkAsync(ct)
                ?? throw new SandboxEngineException(
                    "egress internal network unavailable");
            if (egressProxy is null)
            {
                throw new SandboxEngineException(
                    "egress proxy service unavailable — allowlist mode " +
                    "cannot start sandboxes without enforcement");
            }

            await egressProxy.EnsureProxyAsync(net, ct);
            env.AddRange(EgressProxyService.ProxyEnv());
            networking = InternalNetworking(net);
        }
        else if (egress.IsRestricted)
        {
            var net = await egress.EnsureNetworkAsync(ct);
            if (net is not null)
            {
                networking = InternalNetworking(net);
            }
        }

        var binds = new List<string>(request.Binds);
        if (request.WarmWorkspace)
        {
            binds.Add(
                $"{workspaces.PrepareWarmPath()}:{Domain.Sandbox.SandboxSpec.WorkspacePath}:rw");
        }
        else if (request.WorkspaceConversationId is { } convId)
        {
            var hostPath = await workspaces.PrepareAsync(convId, ct);
            binds.Add(
                $"{hostPath}:{Domain.Sandbox.SandboxSpec.WorkspacePath}:rw");
        }

        // DinD alternativa fraca: socket mount explícito (SPEC-14 RF-002).
        if (options.Value.DockerSocketMount && !options.Value.EnableDinD)
        {
            binds.Add("/var/run/docker.sock:/var/run/docker.sock");
        }

        var labels = new Dictionary<string, string>(request.Labels)
        {
            [EngineLabels.Managed] = "true",
            [EngineLabels.Owner] = options.Value.InstanceOwner,
        };

        var hostConfig = BuildHostConfig(request, options.Value, binds);

        var container = await docker.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = request.Image,
                Name = request.Name,
                Labels = labels,
                Env = env,
                HostConfig = hostConfig,
                NetworkingConfig = networking,
                WorkingDir = request.WorkingDir ?? string.Empty,
                Tty = false,
                Cmd = request.Cmd?.ToList() ?? [],
            }, ct);

        await docker.Containers.StartContainerAsync(container.ID, null, ct);

        // DinD opt-in (SPEC-14 RF-002): sidecar privileged compartilhando
        // o netns do sandbox → DOCKER_HOST=tcp://localhost:2375.
        if (options.Value.EnableDinD)
        {
            await StartDinDSidecarAsync(container.ID, ct);
        }

        // SPEC-35: control network compartilhada — o Api alcança o agente
        // pelo IP nesta rede (sem publicar porta no host).
        if (options.Value.ControlNetwork is { } controlNet)
        {
            try
            {
                await EnsureNetworkExistsAsync(controlNet, ct);
                await docker.Networks.ConnectNetworkAsync(controlNet,
                    new NetworkConnectParameters { Container = container.ID }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "control network attach failed for {ContainerId}",
                    container.ID[..12]);
            }
        }

        var info = await InspectAsync(container.ID, ct)
            ?? throw new SandboxEngineException(
                $"container {container.ID} gone right after create");
        logger.LogInformation(
            "engine started sandbox container {ContainerId} (image {Image})",
            container.ID[..12], request.Image);
        return info;
    }

    /// <summary>
    /// Builds the sandbox <see cref="HostConfig"/> — limits, binds, optional
    /// OCI runtime (SPEC-86) and agent port publishing. Internal static so
    /// unit tests can verify the mapping without a Docker daemon.
    /// </summary>
    internal static HostConfig BuildHostConfig(
        EngineCreateRequest request, SandboxOptions options,
        IList<string> binds)
    {
        var hostConfig = new HostConfig
        {
            Memory = request.MemoryBytes,
            NanoCPUs = request.NanoCpus,
            PidsLimit = request.PidsLimit,
            Binds = binds,
            AutoRemove = false,
        };
        if (!string.IsNullOrWhiteSpace(options.DockerRuntime))
        {
            // SPEC-86: sysbox-runc etc. — stronger in-sandbox isolation
            // (userns) without the privileged DinD sidecar.
            hostConfig.Runtime = options.DockerRuntime;
        }
        if (request.IsAgentImage && options.PublishAgentPort)
        {
            // Docker Desktop / daemon remoto: publica 8000; HostIP 0.0.0.0
            // quando Sandbox:AgentHost aponta para um host acessível.
            hostConfig.PortBindings = new Dictionary<string, IList<PortBinding>>
            {
                ["8000/tcp"] = [new PortBinding
                {
                    HostIP = options.AgentHost is null
                        ? "127.0.0.1" : "0.0.0.0",
                    HostPort = "",
                }],
            };
        }
        return hostConfig;
    }

    /// <inheritdoc />
    public async Task<EngineSandboxInfo?> InspectAsync(
        string containerId, CancellationToken ct)
    {
        try
        {
            var inspect = await docker.Containers.InspectContainerAsync(
                containerId, ct);
            return new EngineSandboxInfo(
                inspect.ID,
                inspect.State?.Status ?? "unknown",
                inspect.State?.Error,
                inspect.Config?.Labels is { } l
                    ? new Dictionary<string, string>(l)
                    : new Dictionary<string, string>(),
                AgentUrlOf(inspect));
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EngineSandboxInfo>> ListManagedAsync(
        CancellationToken ct)
    {
        var containers = await docker.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool>
                    {
                        [$"{EngineLabels.Managed}=true"] = true,
                        [$"{EngineLabels.Owner}={options.Value.InstanceOwner}"]
                            = true,
                    },
                },
            }, ct);
        return [.. containers
            .Where(c => !IsPoolContainer(c))
            .Select(ToInfo)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EngineSandboxInfo>> ListPoolAsync(
        CancellationToken ct)
    {
        var containers = await docker.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool>
                    {
                        [$"{EngineLabels.Managed}=true"] = true,
                        [$"{EngineLabels.Owner}={options.Value.InstanceOwner}"]
                            = true,
                        [$"{EngineLabels.Pool}=warm"] = true,
                    },
                },
            }, ct);
        return [.. containers.Where(IsPoolContainer).Select(ToInfo)];
    }

    /// <inheritdoc />
    public async Task<EngineWarmClaim?> ClaimWarmAsync(
        WarmClaimRequest request, CancellationToken ct)
    {
        // SPEC-19 RF-005: warm containers nascem sem proxy env — em modo
        // allowlist o claim é impossível (env é imutável) → cold start.
        if (egress.IsAllowlist)
        {
            return null;
        }

        var pool = await docker.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                // All=true: slot ocupado mesmo que o container tenha saído
                // (ex.: stub sem TTY nos testes) — claim re-start abaixo.
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool>
                    {
                        [$"{EngineLabels.Managed}=true"] = true,
                        [$"{EngineLabels.Owner}={options.Value.InstanceOwner}"]
                            = true,
                        [$"{EngineLabels.Pool}=warm"] = true,
                        [$"{EngineLabels.PoolKey}={request.Image}"] = true,
                    },
                },
            }, ct);
        var free = pool.FirstOrDefault(IsPoolContainer);
        if (free is null)
        {
            return null;
        }

        try
        {
            // Rename é o claim marker (labels são imutáveis): ohn-pool-* →
            // ohn-sandbox-*.
            await docker.Containers.RenameContainerAsync(free.ID,
                new ContainerRenameParameters { NewName = request.SandboxName },
                ct);

            var inspect = await docker.Containers.InspectContainerAsync(
                free.ID, ct);
            if (inspect.State?.Running != true)
            {
                // Slot warm parado (stub de teste sem TTY, crash…) — re-start
                // preserva fs/env/labels (claim não é re-create).
                await docker.Containers.StartContainerAsync(
                    free.ID, null, ct);
            }

            if (request.WorkspaceConversationId is { } convId)
            {
                var wsMount = inspect.Mounts.FirstOrDefault(m =>
                    m.Destination == Domain.Sandbox.SandboxSpec.WorkspacePath);
                if (wsMount is not null)
                {
                    var target = workspaces.PreparePath(convId);
                    if (!Directory.Exists(target))
                    {
                        Directory.Move(wsMount.Source, target);
                    }
                    else
                    {
                        Directory.Delete(wsMount.Source, recursive: true);
                    }
                }

                await workspaces.PrepareAsync(convId, ct);
            }

            var initKey = inspect.Config?.Labels is { } labels
                && labels.TryGetValue(EngineLabels.InitKey, out var k)
                    ? k : null;
            return new EngineWarmClaim(free.ID, initKey, AgentUrlOf(inspect));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e,
                "warm claim of {ContainerId} failed — pool self-heals",
                free.ID[..12]);
            try
            {
                await docker.Containers.RemoveContainerAsync(free.ID,
                    new ContainerRemoveParameters { Force = true }, ct);
            }
            catch (Exception)
            {
                // best-effort cleanup; pool prune/sweep picks leftovers
            }

            return null;
        }
    }

    /// <inheritdoc />
    public Task PauseAsync(string containerId, CancellationToken ct) =>
        Translate(() => docker.Containers.PauseContainerAsync(containerId, ct));

    /// <inheritdoc />
    public Task UnpauseAsync(string containerId, CancellationToken ct) =>
        Translate(() => docker.Containers.UnpauseContainerAsync(containerId, ct));

    /// <inheritdoc />
    public Task StartAsync(string containerId, CancellationToken ct) =>
        Translate(() => docker.Containers.StartContainerAsync(
            containerId, null, ct));

    /// <inheritdoc />
    public Task StopAsync(string containerId, CancellationToken ct) =>
        Translate(() => docker.Containers.StopContainerAsync(
            containerId, new ContainerStopParameters(), ct));

    /// <inheritdoc />
    /// <remarks>
    /// Workspace do provider docker é um bind do host — removido pelo
    /// <c>WorkspaceProvisioner.RemoveWorkspace</c> (SPEC-88); o parâmetro
    /// <c>workspaceConversationId</c> aplica-se apenas a workspaces
    /// externos (PVC no k8s).
    /// </remarks>
    public async Task RemoveAsync(
        string containerId, CancellationToken ct,
        Guid? workspaceConversationId = null)
    {
        try
        {
            await docker.Containers.RemoveContainerAsync(containerId,
                new ContainerRemoveParameters { Force = true }, ct);
        }
        catch (DockerContainerNotFoundException)
        {
            // already gone — delete stays idempotent
        }
        catch (DockerApiException ex)
        {
            // daemon races (ex.: removal already in progress) não bloqueiam
            logger.LogWarning(ex,
                "Container {ContainerId} removal reported an error (ignored)",
                containerId[..12]);
        }
    }

    /// <summary>Image present locally? Pulls when missing.</summary>
    public async Task EnsureImageAsync(string image, CancellationToken ct)
    {
        try
        {
            await docker.Images.InspectImageAsync(image, ct);
        }
        catch (DockerImageNotFoundException ex)
        {
            var (name, tag) = image.Split(':') is [var n, var t]
                ? (n, t) : (image, "latest");
            logger.LogInformation(ex, "Pulling sandbox image {Image}…", image);
            await docker.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = name, Tag = tag },
                null, new Progress<JSONMessage>(), ct);
        }
    }

    /// <summary>
    /// Agent base URL for a container (SPEC-17 RF-003): control-network IP
    /// first (SPEC-35 split mode), published port next (Desktop/remote
    /// daemon), else first bridge IP.
    /// </summary>
    public Uri? AgentUrlOf(ContainerInspectResponse inspect)
    {
        const int agentPort = 8000;
        var nets = inspect.NetworkSettings?.Networks;
        if (options.Value.ControlNetwork is { } controlNet
            && nets is not null
            && nets.TryGetValue(controlNet, out var ctrl)
            && !string.IsNullOrEmpty(ctrl.IPAddress))
        {
            return new Uri($"http://{ctrl.IPAddress}:{agentPort}");
        }

        var ports = inspect.NetworkSettings?.Ports;
        if (ports is not null
            && ports.TryGetValue($"{agentPort}/tcp", out var bindings)
            && bindings?.Count > 0
            && int.TryParse(bindings[0].HostPort, out var hostPort))
        {
            return new Uri(
                $"http://{options.Value.AgentHost ?? "127.0.0.1"}:{hostPort}");
        }

        if (nets is not null
            && nets.Values.FirstOrDefault(n =>
                !string.IsNullOrEmpty(n.IPAddress)) is { IPAddress: { } ip })
        {
            return new Uri($"http://{ip}:{agentPort}");
        }

        return null;
    }

    private static bool IsPoolContainer(ContainerListResponse c) =>
        c.Names.Any(n => n.TrimStart('/')
            .StartsWith(EngineLabels.PoolNamePrefix, StringComparison.Ordinal));

    private static EngineSandboxInfo ToInfo(ContainerListResponse c) => new(
        c.ID, c.State, null,
        c.Labels is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(c.Labels), null,
        c.Created == default ? null : c.Created);

    private static NetworkingConfig InternalNetworking(string net) => new()
    {
        EndpointsConfig = new Dictionary<string, EndpointSettings>
        {
            [net] = new(),
        },
    };

    private async Task EnsureNetworkExistsAsync(string name, CancellationToken ct)
    {
        var nets = await docker.Networks.ListNetworksAsync(
            new NetworksListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["name"] = new Dictionary<string, bool> { [name] = true },
                },
            }, ct);
        if (nets.Any(n => n.Name == name))
        {
            return;
        }

        await docker.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = name,
            Driver = "bridge",
            Labels = new Dictionary<string, string>
            {
                [EngineLabels.Managed] = "true",
                [EngineLabels.Owner] = options.Value.InstanceOwner,
            },
        }, ct);
    }

    /// <summary>
    /// DinD sidecar sharing the sandbox net namespace (SPEC-14 RF-002):
    /// privileged <c>docker:dind</c> + shared workspace bind; the sandbox
    /// reaches the daemon via <c>DOCKER_HOST=tcp://localhost:2375</c>.
    /// </summary>
    private async Task StartDinDSidecarAsync(
        string sandboxContainerId, CancellationToken ct)
    {
        const string dindImage = "docker:27-dind";
        await EnsureImageAsync(dindImage, ct);
        var inspect = await docker.Containers
            .InspectContainerAsync(sandboxContainerId, ct);
        var wsMount = inspect.Mounts.FirstOrDefault(m =>
            m.Destination == Domain.Sandbox.SandboxSpec.WorkspacePath);
        var binds = wsMount is null ? []
            : new List<string>
            {
                $"{wsMount.Source}:{Domain.Sandbox.SandboxSpec.WorkspacePath}:rw",
            };
        var sidecar = await docker.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = dindImage,
                Name = $"ohn-dind-{sandboxContainerId[..12]}",
                Labels = new Dictionary<string, string>
                {
                    [EngineLabels.Managed] = "true",
                    [EngineLabels.Owner] = options.Value.InstanceOwner,
                    [EngineLabels.DinDFor] = sandboxContainerId,
                },
                HostConfig = new HostConfig
                {
                    Privileged = true,
                    Binds = binds,
                    NetworkMode = $"container:{sandboxContainerId}",
                    AutoRemove = true,
                },
                Env = ["DOCKER_TLS_CERTDIR="],
            }, ct);
        await docker.Containers.StartContainerAsync(sidecar.ID, null, ct);
        logger.LogInformation("DinD sidecar {SidecarId} for sandbox {ContainerId}",
            sidecar.ID[..12], sandboxContainerId[..12]);
    }

    private static async Task Translate(Func<Task> op)
    {
        try
        {
            await op();
        }
        catch (DockerContainerNotFoundException ex)
        {
            throw new SandboxEngineException(
                "container not found", notFound: true, inner: ex);
        }
        catch (DockerApiException ex) when (
            ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new SandboxEngineException(
                ex.Message, notFound: true, inner: ex);
        }
    }
}