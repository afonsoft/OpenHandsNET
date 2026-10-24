using System.Net.Http.Json;
using System.Text.Json;

using Docker.DotNet;
using Docker.DotNet.Models;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Sandboxing;

using StackExchange.Redis;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-36 DoD: e2e multi-réplica real — duas instâncias do Api
/// (WebApplicationFactory) compartilhando um Valkey/Redis de verdade
/// (container Docker.DotNet — mesmo padrão de <see cref="Persistence.PostgresMigrationsTests"/>;
/// Testcontainers não é usado por incompatibilidade com Docker.DotNet.Enhanced).
/// Cada [Fact] levanta um Valkey próprio + par de réplicas com
/// <c>Cache:Redis:InstanceName</c> único. Skip silencioso sem daemon Docker.
/// </summary>
public sealed class Spec36MultiReplicaTests : IAsyncLifetime
{
    private const string Image = "valkey/valkey:8-alpine";
    private static readonly bool Docker = DockerAvailable();

    private IDockerClient? _client;
    private string? _containerId;
    private string? _redis;
    private readonly string _instance = $"ohn-it-{Guid.NewGuid():N}:";
    private readonly List<string> _dbPaths = [];
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public async Task InitializeAsync()
    {
        if (!Docker)
        {
            return;
        }

        _client = DockerClientFactory.Create();
        await _client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = Image },
            new AuthConfig(), new Progress<JSONMessage>());
        var created = await _client.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = Image,
                HostConfig = new HostConfig { PublishAllPorts = true },
            });
        _containerId = created.ID;
        await _client.Containers.StartContainerAsync(
            _containerId, new ContainerStartParameters());

        var inspect = await _client.Containers.InspectContainerAsync(_containerId);
        var port = inspect.NetworkSettings!.Ports["6379/tcp"][0].HostPort;
        _redis = $"localhost:{port}";

        // Espera o Valkey aceitar conexões.
        for (var i = 0; i < 60; i++)
        {
            try
            {
                await using var mux = await ConnectionMultiplexer.ConnectAsync(_redis);
                await mux.GetDatabase().PingAsync();
                return;
            }
            catch
            {
                await Task.Delay(500);
            }
        }

        throw new InvalidOperationException("valkey container did not become ready");
    }

    public async Task DisposeAsync()
    {
        foreach (var f in _factories)
        {
            await f.DisposeAsync();
        }

        if (_client is not null && _containerId is not null)
        {
            try
            {
                await _client.Containers.RemoveContainerAsync(
                    _containerId, new ContainerRemoveParameters { Force = true });
            }
            catch (DockerApiException)
            {
            }
        }

        _client?.Dispose();
        foreach (var db in _dbPaths)
        {
            if (File.Exists(db)) File.Delete(db);
        }
    }

    /// <summary>Boota uma réplica do Api apontando para o Valkey do teste.</summary>
    private WebApplicationFactory<Program> NewReplica(
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        var db = Path.Combine(
            Path.GetTempPath(), $"ohn-spec36-{Guid.NewGuid():N}.db");
        _dbPaths.Add(db);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Database:Provider", "Sqlite");
            b.UseSetting("Database:ConnectionString", $"Data Source={db}");
            b.UseSetting("Observability:OtlpEndpoint", string.Empty);
            b.UseSetting("Sandbox:DefaultImage", "bash:5");
            b.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            b.UseSetting("Cache:Provider", "redis");
            b.UseSetting("Cache:Redis:ConnectionString", _redis!);
            b.UseSetting("Cache:Redis:InstanceName", _instance);
            b.UseSetting("Deployment:Replicas", "2");
            if (extra is not null)
            {
                foreach (var (k, v) in extra)
                {
                    b.UseSetting(k, v);
                }
            }
        });
        _factories.Add(factory);
        // Força o boot (hosted services + mux) já no setup do teste.
        _ = factory.Server;
        return factory;
    }

    // SPEC-97: hub protegido — dev-login fornece o cookie do negotiate.
    private static async Task<HubConnection> ConnectAsync(
        WebApplicationFactory<Program> replica)
    {
        using var http = replica.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var res = await http.PostAsJsonAsync(
            "/api/v1/auth/dev-login",
            new { email = "hub@local", displayName = "hub" });
        res.EnsureSuccessStatusCode();
        var cookie = res.Headers.GetValues("Set-Cookie")
            .First().Split(';')[0];

        return new HubConnectionBuilder()
            .WithUrl(new Uri(replica.Server.BaseAddress, "/hubs/conversations"),
                o =>
                {
                    o.HttpMessageHandlerFactory =
                        _ => replica.Server.CreateHandler();
                    o.Headers.Add("Cookie", cookie);
                })
            .Build();
    }

    /// <summary>DoD: evento publicado na réplica A chega ao subscriber da B.</summary>
    [Fact]
    public async Task Events_On_ReplicaA_Reach_Subscriber_On_ReplicaB()
    {
        if (_client is null)
        {
            return;
        }

        var replicaA = NewReplica();
        var replicaB = NewReplica();
        var convId = Guid.NewGuid();

        await using var conn = await ConnectAsync(replicaB);
        await conn.StartAsync();
        var received = new List<string>();
        var tcs = new TaskCompletionSource();
        var stream = conn.StreamAsync<JsonElement>(
            "JoinConversation", convId, (string?)null);
        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in stream)
                {
                    received.Add(e.GetProperty("id").GetString()!);
                    if (received.Count == 2) tcs.TrySetResult();
                }
            }
            catch (OperationCanceledException)
            {
                // StopAsync cancela o stream — esperado no teardown.
            }
        });

        // Garante que o subscribe do DistributedEventBus da réplica B já
        // chegou ao Redis antes de publicar na A.
        await Task.Delay(500);

        using var scope = replicaA.Services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventService>();
        var ids = new List<string>();
        foreach (var text in new[] { "cross-replica-1", "cross-replica-2" })
        {
            var e = await events.AppendAsync(convId, new MessageEvent
            {
                Source = EventSource.User,
                LlmMessage = JsonSerializer.SerializeToElement(
                    new { role = "user", content = new[] { new { type = "text", text } } }),
            }, CancellationToken.None);
            ids.Add(e.Id);
        }

        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        done.ShouldBe(tcs.Task, "replica B did not receive events published on A");
        received.ShouldBe(ids);
        await conn.StopAsync();
        await reader;
    }

    /// <summary>DoD: rate limit é global entre réplicas (não por-réplica).</summary>
    [Fact]
    public async Task RateLimit_Is_Global_Across_Replicas()
    {
        if (_client is null)
        {
            return;
        }

        var cfg = new Dictionary<string, string?>
        {
            ["RateLimit:RequestsPerMinute"] = "2",
        };
        var replicaA = NewReplica(cfg);
        var replicaB = NewReplica(cfg);
        using var clientA = replicaA.CreateClient();
        using var clientB = replicaB.CreateClient();

        // SPEC-97: /api/v1/config é AllowAnonymous → chega ao limiter como
        // "ip:" (mesma identidade anônima nos dois lados). A janela fixa
        // global (2/min) tem que contar o cluster inteiro — endpoints
        // protegidos devolveriam 401 antes do limiter e não mediriam nada.
        (await clientA.GetAsync("/api/v1/config"))
            .StatusCode.ShouldNotBe(System.Net.HttpStatusCode.TooManyRequests);
        (await clientA.GetAsync("/api/v1/config"))
            .StatusCode.ShouldNotBe(System.Net.HttpStatusCode.TooManyRequests);
        // Com contador por-réplica a B ainda teria quota; global → 429.
        (await clientB.GetAsync("/api/v1/config"))
            .StatusCode.ShouldBe(System.Net.HttpStatusCode.TooManyRequests);
    }

    /// <summary>DoD: Redis derrubado → healthz degraded + requests continuam.</summary>
    [Fact]
    public async Task Redis_Down_Reports_Degraded_And_Api_Still_Serves()
    {
        if (_client is null)
        {
            return;
        }

        var replicaA = NewReplica();
        var mux = replicaA.Services.GetRequiredService<IConnectionMultiplexer>();
        mux.IsConnected.ShouldBeTrue("mux should start connected");

        await _client.Containers.StopContainerAsync(
            _containerId!, new ContainerStopParameters());

        // Heartbeat do SE.Redis leva alguns segundos para marcar down.
        for (var i = 0; i < 40 && mux.IsConnected; i++)
        {
            await Task.Delay(500);
        }

        mux.IsConnected.ShouldBeFalse("mux never noticed the redis outage");

        using var client = await TestAuth.LoginAsync(replicaA);
        var readiness = await client.GetAsync("/api/v1/readiness");
        var payload = await readiness.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("checks").GetProperty("cache")
            .GetString().ShouldBe("degraded");

        // RF-004: fail-open — liveness e pipeline de request seguem de pé.
        (await client.GetAsync("/api/v1/health")).EnsureSuccessStatusCode();
        await client.GetAsync("/api/v1/settings");
    }

    private static bool DockerAvailable()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("docker", "info")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}