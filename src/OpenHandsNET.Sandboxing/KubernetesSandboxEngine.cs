using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Sandbox;

namespace OpenHandsNET.Sandboxing;

/// <summary>SPEC-37 RF-001: <c>Sandbox:Kubernetes:*</c> options.</summary>
public sealed class KubernetesOptions
{
    /// <summary>Namespace dos pods de sandbox (RBAC mínimo scopeado).</summary>
    public string Namespace { get; set; } = "openhandsnet-sandboxes";

    /// <summary>kubeconfig path; null = in-cluster (ServiceAccount).</summary>
    public string? KubeconfigPath { get; set; }

    /// <summary>SPEC-37: Sysbox runtime p/ DinD (runtimeClassName).</summary>
    public string? RuntimeClassName { get; set; } = "sysbox-runc";

    /// <summary>Template de PVC por sandbox (<c>{id}</c> substituído).</summary>
    public string WorkspacePvcTemplate { get; set; } = "ohn-ws-{id}";

    /// <summary>StorageClass do workspace PVC (null = default do cluster).</summary>
    public string? WorkspaceStorageClass { get; set; }

    /// <summary>Tamanho do PVC de workspace.</summary>
    public string WorkspacePvcSize { get; set; } = "4Gi";
}

/// <summary>
/// SPEC-37: <see cref="ISandboxEngine"/> sobre Kubernetes — 1 pod por
/// sandbox (Sysbox p/ DinD), PVC por workspace. Selecionado por
/// <c>Sandbox:Provider=kubernetes</c>; também pode viver atrás do
/// runtime-api (SPEC-35). Warm-pool fica fora de escopo no MVP k8s
/// (claim devolve null → cold start).
/// </summary>
public sealed class KubernetesSandboxEngine(
    IOptions<SandboxOptions> options,
    IOptions<KubernetesOptions> k8sOptions,
    ILogger<KubernetesSandboxEngine> logger) : ISandboxEngine
{
    private readonly IKubernetes _k8s = new Kubernetes(
        k8sOptions.Value.KubeconfigPath is { } kc
            ? KubernetesClientConfiguration.BuildConfigFromConfigFile(kc)
            : KubernetesClientConfiguration.InClusterConfig());

    private string Ns => k8sOptions.Value.Namespace;

    /// <inheritdoc />
    public bool IsRemote => false;

    /// <inheritdoc />
    public async Task<EngineSandboxInfo> CreateAndStartAsync(
        EngineCreateRequest request, CancellationToken ct)
    {
        var ko = k8sOptions.Value;
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = request.Name, NamespaceProperty = Ns,
                Labels = new Dictionary<string, string>(request.Labels)
                {
                    [EngineLabels.Managed] = "true",
                    [EngineLabels.Owner] = options.Value.InstanceOwner,
                },
            },
            Spec = new V1PodSpec
            {
                RestartPolicy = "Never",
                RuntimeClassName = request.IsAgentImage
                    ? ko.RuntimeClassName : null,
                Containers =
                [
                    new V1Container
                    {
                        Name = "sandbox",
                        Image = request.Image,
                        Command = request.Cmd?.ToList(),
                        WorkingDir = request.WorkingDir,
                        Env = [.. request.Env.Select(e =>
                            new V1EnvVar(e.Split('=', 2)[0],
                                e.Split('=', 2)[1]))],
                        Resources = new V1ResourceRequirements
                        {
                            Limits = new Dictionary<string, ResourceQuantity>
                            {
                                ["memory"] = new($"{request.MemoryBytes}"),
                                ["cpu"] = new(
                                    $"{request.NanoCpus / 1_000_000_000.0:F2}"),
                            },
                        },
                        Ports = request.IsAgentImage
                            ? [new V1ContainerPort(8000)] : null,
                    },
                ],
            },
        };

        if (request.WorkspaceConversationId is { } convId)
        {
            var pvc = $"ohn-ws-{convId:N}";
            await EnsurePvcAsync(pvc, ct);
            pod.Spec.Volumes =
            [
                new V1Volume
                {
                    Name = "workspace",
                    PersistentVolumeClaim =
                        new V1PersistentVolumeClaimVolumeSource
                        {
                            ClaimName = pvc,
                        },
                },
            ];
            pod.Spec.Containers[0].VolumeMounts =
                [new V1VolumeMount("/workspace", "workspace")];
        }

        var created = await _k8s.CoreV1
            .CreateNamespacedPodAsync(pod, Ns, cancellationToken: ct);
        return ToInfo(created);
    }

    /// <inheritdoc />
    public async Task<EngineSandboxInfo?> InspectAsync(
        string name, CancellationToken ct)
    {
        try
        {
            var pod = await _k8s.CoreV1.ReadNamespacedPodAsync(
                name, Ns, cancellationToken: ct);
            return ToInfo(pod);
        }
        catch (k8s.Autorest.HttpOperationException e)
            when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EngineSandboxInfo>> ListManagedAsync(
        CancellationToken ct) =>
        (await _k8s.CoreV1.ListNamespacedPodAsync(Ns,
            labelSelector: $"{EngineLabels.Managed}=true",
            cancellationToken: ct)).Items
            .Where(p => !p.Metadata.Name.StartsWith(
                EngineLabels.PoolNamePrefix, StringComparison.Ordinal))
            .Select(ToInfo).ToList();

    /// <inheritdoc />
    public async Task<IReadOnlyList<EngineSandboxInfo>> ListPoolAsync(
        CancellationToken ct) =>
        (await _k8s.CoreV1.ListNamespacedPodAsync(Ns,
            labelSelector: $"{EngineLabels.Managed}=true",
            cancellationToken: ct)).Items
            .Where(p => p.Metadata.Name.StartsWith(
                EngineLabels.PoolNamePrefix, StringComparison.Ordinal))
            .Select(ToInfo).ToList();

    /// <inheritdoc />
    public Task<EngineWarmClaim?> ClaimWarmAsync(
        WarmClaimRequest request, CancellationToken ct) =>
        // MVP k8s: sem warm pool — cold start sempre.
        Task.FromResult<EngineWarmClaim?>(null);

    /// <inheritdoc />
    public async Task PauseAsync(string name, CancellationToken ct) =>
        // K8s não tem pause nativo — suspend via annotation é no-op aqui;
        // SandboxService já tem fallback p/ stop.
        throw new SandboxEngineException("pause unsupported on kubernetes");

    /// <inheritdoc />
    public Task UnpauseAsync(string name, CancellationToken ct) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task StartAsync(string name, CancellationToken ct) =>
        // Pods são criados running; restart de um parado = falha do
        // chamador para recriar (K8s pods não são restartáveis).
        Task.CompletedTask;

    /// <inheritdoc />
    public async Task StopAsync(string name, CancellationToken ct) =>
        await _k8s.CoreV1.DeleteNamespacedPodAsync(
            name, Ns, gracePeriodSeconds: 5, cancellationToken: ct);

    /// <inheritdoc />
    public async Task RemoveAsync(string name, CancellationToken ct)
    {
        try
        {
            await _k8s.CoreV1.DeleteNamespacedPodAsync(
                name, Ns, cancellationToken: ct);
        }
        catch (k8s.Autorest.HttpOperationException e)
            when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // idempotent
        }
    }

    private async Task EnsurePvcAsync(string name, CancellationToken ct)
    {
        try
        {
            await _k8s.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(
                name, Ns, cancellationToken: ct);
            return;
        }
        catch (k8s.Autorest.HttpOperationException e)
            when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }

        var ko = k8sOptions.Value;
        await _k8s.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(
            new V1PersistentVolumeClaim
            {
                Metadata = new V1ObjectMeta
                {
                    Name = name, NamespaceProperty = Ns,
                    Labels = new Dictionary<string, string>
                    {
                        [EngineLabels.Managed] = "true",
                    },
                },
                Spec = new V1PersistentVolumeClaimSpec
                {
                    AccessModes = ["ReadWriteOnce"],
                    StorageClassName = ko.WorkspaceStorageClass,
                    Resources = new V1VolumeResourceRequirements
                    {
                        Requests = new Dictionary<string, ResourceQuantity>
                        {
                            ["storage"] = new(ko.WorkspacePvcSize),
                        },
                    },
                },
            }, Ns, cancellationToken: ct);
        logger.LogInformation("workspace PVC {Pvc} created", name);
    }

    private static EngineSandboxInfo ToInfo(V1Pod pod)
    {
        var phase = pod.Status?.Phase ?? "Pending";
        var state = phase switch
        {
            "Running" => "running",
            "Succeeded" or "Failed" => "exited",
            _ => "created",
        };
        var ip = pod.Status?.PodIP;
        return new EngineSandboxInfo(
            pod.Metadata.Name, state,
            pod.Status?.ContainerStatuses?.FirstOrDefault()?.State?.Waiting
                ?.Reason,
            pod.Metadata.Labels is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(pod.Metadata.Labels),
            ip is null ? null : new Uri($"http://{ip}:8000"));
    }
}
