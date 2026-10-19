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

    /// <summary>
    /// SPEC-49 RF-001: warm pool por imagem — formato
    /// <c>imgA:2;imgB:1</c> (default vazio = desligado; o reconciler do
    /// <see cref="WarmPoolService"/> mantém N pods dormant por imagem).
    /// </summary>
    public string WarmPoolImages { get; set; } = "";

    /// <summary>TTL de pod warm não-claimed (orphan sweep; default 30min).</summary>
    public int WarmPodTtlMinutes { get; set; } = 30;
}

/// <summary>
/// SPEC-37 + SPEC-49: <see cref="ISandboxEngine"/> sobre Kubernetes — 1 pod
/// por sandbox (Sysbox p/ DinD), PVC por workspace.
/// <para>
/// SPEC-49 RF-002: pods não são pausáveis/restartáveis — sleep e stop
/// fazem snapshot do <see cref="V1Pod"/> serializado num ConfigMap
/// <c>ohn-sleep-{name}</c> e deletam o pod; o PVC de workspace
/// (<c>ohn-ws-*</c>) é retido. Resume/start recria o pod a partir do
/// snapshot montado sobre o mesmo PVC → workspace preservado.
/// </para>
/// <para>
/// SPEC-49 RF-001: warm claim — pod names/volumes são imutáveis, então o
/// claim deleta o pod warm e o recria sob <c>SandboxName</c> com as
/// labels de claim e a PVC da conversa (imagem já puxada no node →
/// claim em ~segundos, deferred-init via <c>/api/init</c> igual ao
/// provider Docker).
/// </para>
/// </summary>
public sealed class KubernetesSandboxEngine(
    IOptions<SandboxOptions> options,
    IOptions<KubernetesOptions> k8sOptions,
    ILogger<KubernetesSandboxEngine> logger,
    IKubernetes? kubernetes = null) : ISandboxEngine
{
    private readonly IKubernetes _k8s = kubernetes ?? new Kubernetes(
        k8sOptions.Value.KubeconfigPath is { } kc
            ? KubernetesClientConfiguration.BuildConfigFromConfigFile(kc)
            : KubernetesClientConfiguration.InClusterConfig());

    private string Ns => k8sOptions.Value.Namespace;

    private static string SleepConfigMapName(string podName) =>
        $"ohn-sleep-{podName}";

    /// <inheritdoc />
    public bool IsRemote => false;

    /// <inheritdoc />
    public bool SupportsPause => true;

    /// <inheritdoc />
    public async Task<EngineSandboxInfo> CreateAndStartAsync(
        EngineCreateRequest request, CancellationToken ct)
    {
        var ko = k8sOptions.Value;
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = request.Name,
                NamespaceProperty = Ns,
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
            AttachWorkspace(pod, pvc);
        }

        var created = await _k8s.CoreV1
            .CreateNamespacedPodAsync(pod, Ns, cancellationToken: ct);
        return ToInfo(created);
    }

    private static void AttachWorkspace(V1Pod pod, string pvc)
    {
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

    /// <inheritdoc />
    public async Task<EngineSandboxInfo?> InspectAsync(
        string name, CancellationToken ct)
    {
        var pod = await ReadPodAsync(name, ct);
        return pod is null ? null : ToInfo(pod);
    }

    private async Task<V1Pod?> ReadPodAsync(string name, CancellationToken ct)
    {
        try
        {
            return await _k8s.CoreV1.ReadNamespacedPodAsync(
                name, Ns, cancellationToken: ct);
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
    public async Task<EngineWarmClaim?> ClaimWarmAsync(
        WarmClaimRequest request, CancellationToken ct)
    {
        // Free slot: pod ohn-pool-* warm servindo a imagem pedida.
        var pool = (await _k8s.CoreV1.ListNamespacedPodAsync(Ns,
            labelSelector: $"{EngineLabels.Managed}=true"
                + $",{EngineLabels.Pool}=warm"
                + $",{EngineLabels.PoolKey}={request.Image}",
            cancellationToken: ct)).Items;
        var free = pool.FirstOrDefault(p => p.Spec is not null);
        if (free is null)
        {
            return null;
        }

        try
        {
            var initKey = free.Metadata.Labels is { } l
                && l.TryGetValue(EngineLabels.InitKey, out var k)
                    ? k : null;

            // Pod names/volumes são imutáveis → claim = recriar o pod sob
            // o nome da sandbox com a PVC da conversa. Imagem já está no
            // node (pod warm rodou ali) → claim ~segundos (SPEC-17 ≤5s).
            await _k8s.CoreV1.DeleteNamespacedPodAsync(
                free.Metadata.Name, Ns, cancellationToken: ct);

            var labels = new Dictionary<string, string>(
                free.Metadata.Labels ?? new Dictionary<string, string>())
            {
                [EngineLabels.Managed] = "true",
                [EngineLabels.Owner] = options.Value.InstanceOwner,
            };
            labels.Remove(EngineLabels.Pool);
            labels.Remove(EngineLabels.PoolKey);
            if (request.WorkspaceConversationId is { } convId)
            {
                labels[EngineLabels.ConversationId] = convId.ToString();
            }

            var pod = new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = request.SandboxName,
                    NamespaceProperty = Ns,
                    Labels = labels,
                },
                Spec = free.Spec,
            };
            pod.Spec.NodeName = null; // libera scheduling livre
            pod.Spec.Volumes = free.Spec.Volumes?
                .Where(v => v.Name != "workspace").ToList();
            pod.Spec.Containers[0].VolumeMounts =
                free.Spec.Containers[0].VolumeMounts?
                    .Where(m => m.Name != "workspace").ToList();

            if (request.WorkspaceConversationId is { } ws)
            {
                var pvc = $"ohn-ws-{ws:N}";
                await EnsurePvcAsync(pvc, ct);
                AttachWorkspace(pod, pvc);
            }

            var created = await _k8s.CoreV1.CreateNamespacedPodAsync(
                pod, Ns, cancellationToken: ct);

            // Health: espera o podIP (≤5s — imagem warm no node).
            var claimed = await WaitPodIpAsync(
                created.Metadata.Name,
                TimeSpan.FromSeconds(5), ct);
            return new EngineWarmClaim(
                created.Metadata.Name, initKey,
                claimed?.Status?.PodIP is { } ip
                    ? new Uri($"http://{ip}:8000") : null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e,
                "warm claim of {Pod} failed — pool self-heals",
                free.Metadata.Name);
            try
            {
                await _k8s.CoreV1.DeleteNamespacedPodAsync(
                    free.Metadata.Name, Ns, cancellationToken: ct);
            }
            catch (Exception)
            {
                // best-effort; sweep cobre restos
            }

            return null;
        }
    }

    private async Task<V1Pod?> WaitPodIpAsync(
        string name, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + budget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var pod = await ReadPodAsync(name, ct);
            if (pod?.Status?.PodIP is not null)
            {
                return pod;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        return await ReadPodAsync(name, ct);
    }

    /// <inheritdoc />
    public async Task PauseAsync(string name, CancellationToken ct)
    {
        // SPEC-49 RF-002: sem cgroup pause em pod — snapshot do spec no
        // ConfigMap + delete; o PVC de workspace é retido (resume recria).
        var pod = await ReadPodAsync(name, ct)
            ?? throw new SandboxEngineException(
                $"pod {name} not found", notFound: true);
        await SaveSnapshotAsync(pod, ct);
        await _k8s.CoreV1.DeleteNamespacedPodAsync(
            name, Ns, gracePeriodSeconds: 5, cancellationToken: ct);
        logger.LogInformation(
            "pod {Pod} slept (snapshot {Cm}, PVC retained)", name,
            SleepConfigMapName(name));
    }

    /// <inheritdoc />
    public Task UnpauseAsync(string name, CancellationToken ct) =>
        RecreateFromSnapshotAsync(name, ct);

    /// <inheritdoc />
    public Task StartAsync(string name, CancellationToken ct) =>
        RecreateFromSnapshotAsync(name, ct);

    private async Task RecreateFromSnapshotAsync(
        string name, CancellationToken ct)
    {
        // Já running → idempotente (service pode chamar unpause+start).
        var existing = await ReadPodAsync(name, ct);
        if (existing is not null)
        {
            return;
        }

        var cmName = SleepConfigMapName(name);
        V1ConfigMap cm;
        try
        {
            cm = await _k8s.CoreV1.ReadNamespacedConfigMapAsync(
                cmName, Ns, cancellationToken: ct);
        }
        catch (k8s.Autorest.HttpOperationException e)
            when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new SandboxEngineException(
                $"pod {name} gone and no sleep snapshot", notFound: true);
        }

        if (!cm.Data.TryGetValue("pod.json", out var json))
        {
            throw new SandboxEngineException(
                $"sleep snapshot {cmName} missing pod.json", notFound: true);
        }

        var pod = KubernetesJson.Deserialize<V1Pod>(json);
        pod.Metadata.ResourceVersion = null;
        pod.Metadata.Uid = null;
        pod.Status = null;
        pod.Spec.NodeName = null; // scheduling livre no wake
        var created = await _k8s.CoreV1.CreateNamespacedPodAsync(
            pod, Ns, cancellationToken: ct);
        await _k8s.CoreV1.DeleteNamespacedConfigMapAsync(
            cmName, Ns, cancellationToken: ct);

        // Wake: espera Running (scheduling + imagem já no node ~segundos);
        // service ResumeAsync inspeciona e espera state=running.
        _ = await WaitRunningAsync(created.Metadata.Name,
            TimeSpan.FromSeconds(60), ct);
        logger.LogInformation("pod {Pod} woke from snapshot {Cm}",
            name, cmName);
    }

    private async Task<V1Pod?> WaitRunningAsync(
        string name, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + budget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var pod = await ReadPodAsync(name, ct);
            if (pod?.Status?.Phase == "Running")
            {
                return pod;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        return await ReadPodAsync(name, ct);
    }

    private async Task SaveSnapshotAsync(V1Pod pod, CancellationToken ct)
    {
        var clean = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = pod.Metadata.Name,
                NamespaceProperty = Ns,
                Labels = pod.Metadata.Labels is null
                    ? null : new Dictionary<string, string>(
                        pod.Metadata.Labels),
                Annotations = pod.Metadata.Annotations is null
                    ? null : new Dictionary<string, string>(
                        pod.Metadata.Annotations),
            },
            Spec = pod.Spec,
        };
        var cm = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta
            {
                Name = SleepConfigMapName(pod.Metadata.Name),
                NamespaceProperty = Ns,
                Labels = new Dictionary<string, string>
                {
                    [EngineLabels.Managed] = "true",
                    [EngineLabels.Role] = "sleep-snapshot",
                },
            },
            Data = new Dictionary<string, string>
            {
                ["pod.json"] = KubernetesJson.Serialize(clean),
            },
        };
        try
        {
            await _k8s.CoreV1.CreateNamespacedConfigMapAsync(
                cm, Ns, cancellationToken: ct);
        }
        catch (k8s.Autorest.HttpOperationException e)
            when (e.Response.StatusCode
                == System.Net.HttpStatusCode.Conflict)
        {
            await _k8s.CoreV1.ReplaceNamespacedConfigMapAsync(
                cm, cm.Metadata.Name, Ns, cancellationToken: ct);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(string name, CancellationToken ct)
    {
        var pod = await ReadPodAsync(name, ct);
        if (pod is null)
        {
            // Sem pod e sem snapshot = gone de verdade (service marca
            // Missing). Snapshot presente = já dormindo → idempotente.
            var cm = await ReadConfigMapAsync(SleepConfigMapName(name), ct);
            if (cm is not null)
            {
                return;
            }

            throw new SandboxEngineException(
                $"pod {name} not found", notFound: true);
        }

        // Stop também preserva workspace: snapshot + delete (resume =
        // StartAsync recria — paridade semântica com docker stop/start).
        await SaveSnapshotAsync(pod, ct);
        await _k8s.CoreV1.DeleteNamespacedPodAsync(
            name, Ns, gracePeriodSeconds: 5, cancellationToken: ct);
    }

    private async Task<V1ConfigMap?> ReadConfigMapAsync(
        string name, CancellationToken ct)
    {
        try
        {
            return await _k8s.CoreV1.ReadNamespacedConfigMapAsync(
                name, Ns, cancellationToken: ct);
        }
        catch (k8s.Autorest.HttpOperationException e)
            when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

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

        try
        {
            await _k8s.CoreV1.DeleteNamespacedConfigMapAsync(
                SleepConfigMapName(name), Ns, cancellationToken: ct);
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
                    Name = name,
                    NamespaceProperty = Ns,
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
            ip is null ? null : new Uri($"http://{ip}:8000"),
            pod.Metadata.CreationTimestamp);
    }
}