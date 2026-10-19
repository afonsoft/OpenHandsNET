using System.Net;
using System.Text;

using k8s;
using k8s.Models;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Sandboxing;

using Shouldly;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-49 — <see cref="KubernetesSandboxEngine"/> sobre um apiserver
/// fake (HttpMessageHandler em memória): warm claim recria o pod sob o
/// nome da sandbox com a PVC da conversa; pause/stop = snapshot em
/// ConfigMap + delete; resume/start recria do snapshot.
/// </summary>
public class Spec49KubernetesEngineTests
{
    private const string Ns = "openhandsnet-sandboxes";

    /// <summary>Apiserver mínimo: pods, PVCs e ConfigMaps por nome.</summary>
    private sealed class FakeKubeHandler : DelegatingHandler
    {
        public Dictionary<string, V1Pod> Pods { get; } = new();
        public Dictionary<string, V1PersistentVolumeClaim> Pvcs { get; } =
            new();
        public Dictionary<string, V1ConfigMap> ConfigMaps { get; } = new();

        private int _ip;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct)
        {
            var res = await HandleAsync(req, ct);
            res.RequestMessage = req;
            return res;
        }

        private async Task<HttpResponseMessage> HandleAsync(
            HttpRequestMessage req, CancellationToken ct)
        {
            var segs = req.RequestUri!.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            var kind = segs[4];
            var name = segs.Length > 5
                ? Uri.UnescapeDataString(segs[5]) : null;
            var method = req.Method.Method;

            if (method == "POST")
            {
                var body = await req.Content!.ReadAsStringAsync(ct);
                return kind switch
                {
                    "pods" => Store(
                        KubernetesJson.Deserialize<V1Pod>(body), Pods,
                        p =>
                        {
                            // Simula scheduling instantâneo.
                            p.Status ??= new V1PodStatus();
                            p.Status.Phase = "Running";
                            p.Status.PodIP = $"10.0.0.{++_ip}";
                        }),
                    "persistentvolumeclaims" => Store(
                        KubernetesJson.Deserialize
                            <V1PersistentVolumeClaim>(body), Pvcs, null),
                    "configmaps" => Store(
                        KubernetesJson.Deserialize<V1ConfigMap>(body),
                        ConfigMaps, null),
                    _ => Status(HttpStatusCode.BadRequest),
                };
            }

            if (method == "PUT")
            {
                var body = await req.Content!.ReadAsStringAsync(ct);
                if (kind == "configmaps" && name is not null)
                {
                    var cm = KubernetesJson.Deserialize<V1ConfigMap>(body);
                    ConfigMaps[name] = cm;
                    return Ok(cm);
                }

                return Status(HttpStatusCode.BadRequest);
            }

            if (method == "GET")
            {
                if (name is null)
                {
                    var selector = req.RequestUri!.Query;
                    return kind switch
                    {
                        "pods" => Ok(new V1PodList
                        {
                            Items = [.. Pods.Values.Where(p =>
                                Matches(p.Metadata?.Labels, selector))],
                        }),
                        "configmaps" => Ok(new V1ConfigMapList
                        {
                            Items = [.. ConfigMaps.Values],
                        }),
                        _ => Status(HttpStatusCode.BadRequest),
                    };
                }

                object? found = kind switch
                {
                    "pods" => Pods.GetValueOrDefault(name),
                    "persistentvolumeclaims" => Pvcs.GetValueOrDefault(name),
                    "configmaps" => ConfigMaps.GetValueOrDefault(name),
                    _ => null,
                };
                return found is null
                    ? Status(HttpStatusCode.NotFound) : Ok(found);
            }

            if (method == "DELETE")
            {
                object? removed = kind switch
                {
                    "pods" => Take(Pods, name!),
                    "persistentvolumeclaims" => Take(Pvcs, name!),
                    "configmaps" => Take(ConfigMaps, name!),
                    _ => null,
                };
                return removed is not null
                    ? Ok(removed) : Status(HttpStatusCode.NotFound);
            }

            return Status(HttpStatusCode.BadRequest);
        }

        private static bool Matches(
            IDictionary<string, string>? labels, string query)
        {
            var sel = query.Split("labelSelector=",
                StringSplitOptions.RemoveEmptyEntries);
            if (sel.Length < 2)
            {
                return true;
            }

            var requirements = Uri.UnescapeDataString(sel[1])
                .Split(',', StringSplitOptions.RemoveEmptyEntries);
            return requirements.All(r =>
            {
                var kv = r.Split('=', 2);
                return labels is not null && kv.Length == 2
                    && labels.TryGetValue(kv[0], out var v)
                    && v == kv[1];
            });
        }

        private static T? Take<T>(Dictionary<string, T> store,
            string name) where T : IKubernetesObject<V1ObjectMeta>
        {
            if (store.Remove(name, out var obj))
            {
                return obj;
            }

            return default;
        }

        private static HttpResponseMessage Store<T>(
            T obj, Dictionary<string, T> store, Action<T>? mutate)
            where T : IKubernetesObject<V1ObjectMeta>
        {
            mutate?.Invoke(obj);
            store[obj.Metadata.Name] = obj;
            return Ok(obj);
        }

        private static HttpResponseMessage Ok(object payload) => new(
            HttpStatusCode.OK)
        {
            Content = new StringContent(
                KubernetesJson.Serialize(payload), Encoding.UTF8,
                "application/json"),
        };

        private static HttpResponseMessage Status(HttpStatusCode code)
        {
            var payload = "{\"kind\":\"Status\",\"status\":\""
                + ((int)code == 200 ? "Success" : "Failure")
                + "\",\"code\":" + (int)code
                + ",\"reason\":\"" + code + "\"}";
            return new HttpResponseMessage(code)
            {
                Content = new StringContent(payload, Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private static KubernetesSandboxEngine NewEngine(
        FakeKubeHandler handler)
    {
        var cfg = new KubernetesClientConfiguration
        {
            Host = "http://127.0.0.1:8080",
            SkipTlsVerify = true,
        };
        var client = new Kubernetes(cfg, handler);
        return new KubernetesSandboxEngine(
            Options.Create(new SandboxOptions { InstanceOwner = "t" }),
            Options.Create(new KubernetesOptions { Namespace = Ns }),
            NullLogger<KubernetesSandboxEngine>.Instance,
            client);
    }

    private static V1Pod WarmPod(string name, string image) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = Ns,
            Labels = new Dictionary<string, string>
            {
                [EngineLabels.Managed] = "true",
                [EngineLabels.Pool] = "warm",
                [EngineLabels.PoolKey] = image,
                [EngineLabels.InitKey] = "init-123",
            },
        },
        Spec = new V1PodSpec
        {
            RestartPolicy = "Never",
            RuntimeClassName = "sysbox-runc",
            Containers =
            [
                new V1Container
                {
                    Name = "sandbox", Image = image,
                    Env =
                    [
                        new V1EnvVar("OHN_DEFERRED_INIT", "true"),
                        new V1EnvVar("OHN_INIT_API_KEY", "init-123"),
                    ],
                },
            ],
        },
        Status = new V1PodStatus { Phase = "Running", PodIP = "10.0.0.9" },
    };

    [Fact]
    public async Task Create_Attaches_Conversation_Pvc()
    {
        var h = new FakeKubeHandler();
        var engine = NewEngine(h);
        var conv = Guid.NewGuid();

        var info = await engine.CreateAndStartAsync(
            new EngineCreateRequest("ohn-sandbox-a", "agent:1", [],
                [], new Dictionary<string, string>(), 1, 1, null,
                null, "/workspace", true, conv), default);

        info.ContainerId.ShouldBe("ohn-sandbox-a");
        h.Pods["ohn-sandbox-a"].Spec.Volumes!.Single()
            .PersistentVolumeClaim!.ClaimName
            .ShouldBe($"ohn-ws-{conv:N}");
        h.Pvcs.ShouldContainKey($"ohn-ws-{conv:N}");
    }

    [Fact]
    public async Task ClaimWarm_Recreates_Under_Sandbox_Name()
    {
        var h = new FakeKubeHandler();
        h.Pods["ohn-pool-1"] = WarmPod("ohn-pool-1", "agent:1");
        var engine = NewEngine(h);
        var conv = Guid.NewGuid();

        var claim = await engine.ClaimWarmAsync(
            new WarmClaimRequest("agent:1", "ohn-sandbox-x", conv),
            default);

        claim.ShouldNotBeNull();
        claim.ContainerId.ShouldBe("ohn-sandbox-x");
        claim.InitKey.ShouldBe("init-123");
        claim.AgentUrl.ShouldNotBeNull(); // podIP fake resolvido
        h.Pods.ShouldNotContainKey("ohn-pool-1");
        var pod = h.Pods["ohn-sandbox-x"];
        pod.Metadata.Labels.ShouldNotContainKey(EngineLabels.Pool);
        pod.Metadata.Labels[EngineLabels.ConversationId]
            .ShouldBe(conv.ToString());
        pod.Spec.Volumes!.Single().PersistentVolumeClaim!.ClaimName
            .ShouldBe($"ohn-ws-{conv:N}");
        h.Pvcs.ShouldContainKey($"ohn-ws-{conv:N}");
    }

    [Fact]
    public async Task ClaimWarm_Miss_Returns_Null()
    {
        var engine = NewEngine(new FakeKubeHandler());
        (await engine.ClaimWarmAsync(
            new WarmClaimRequest("agent:1", "ohn-sandbox-x", Guid.NewGuid()),
            default)).ShouldBeNull();
    }

    [Fact]
    public async Task Pause_Snapshots_And_Start_Recreates()
    {
        var h = new FakeKubeHandler();
        var engine = NewEngine(h);
        var conv = Guid.NewGuid();
        await engine.CreateAndStartAsync(
            new EngineCreateRequest("ohn-sandbox-p", "agent:1", [],
                [], new Dictionary<string, string>(), 1, 1, null,
                null, "/workspace", true, conv), default);

        await engine.PauseAsync("ohn-sandbox-p", default);
        h.Pods.ShouldNotContainKey("ohn-sandbox-p");
        h.ConfigMaps.ShouldContainKey("ohn-sleep-ohn-sandbox-p");
        // PVC retido — workspace preservado entre sleep e wake.
        h.Pvcs.ShouldContainKey($"ohn-ws-{conv:N}");

        await engine.StartAsync("ohn-sandbox-p", default);
        h.Pods.ShouldContainKey("ohn-sandbox-p");
        var woke = h.Pods["ohn-sandbox-p"];
        woke.Spec.Volumes!.Single().PersistentVolumeClaim!.ClaimName
            .ShouldBe($"ohn-ws-{conv:N}");
        h.ConfigMaps.ShouldNotContainKey("ohn-sleep-ohn-sandbox-p");
    }

    [Fact]
    public async Task Start_Without_Snapshot_Is_NotFound()
    {
        var engine = NewEngine(new FakeKubeHandler());
        var ex = await Should.ThrowAsync<SandboxEngineException>(
            () => engine.StartAsync("ohn-sandbox-gone", default));
        ex.NotFound.ShouldBeTrue();
    }

    [Fact]
    public async Task Remove_Cleans_Pod_And_Snapshot()
    {
        var h = new FakeKubeHandler();
        var engine = NewEngine(h);
        await engine.CreateAndStartAsync(
            new EngineCreateRequest("ohn-sandbox-r", "agent:1", [],
                [], new Dictionary<string, string>(), 1, 1, null,
                null, "/workspace", true, Guid.NewGuid()), default);
        await engine.PauseAsync("ohn-sandbox-r", default);

        await engine.RemoveAsync("ohn-sandbox-r", default);
        h.Pods.ShouldNotContainKey("ohn-sandbox-r");
        h.ConfigMaps.ShouldNotContainKey("ohn-sleep-ohn-sandbox-r");
    }

    [Fact]
    public void SupportsPause_Is_True() =>
        NewEngine(new FakeKubeHandler()).SupportsPause.ShouldBeTrue();
}