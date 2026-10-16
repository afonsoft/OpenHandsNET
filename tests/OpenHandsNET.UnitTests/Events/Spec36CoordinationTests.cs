using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenHandsNET.Application.Coordination;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Cache;
using OpenHandsNET.Infrastructure.Eventing;
using Shouldly;
using Xunit;

namespace OpenHandsNET.UnitTests.Events;

/// <summary>
/// SPEC-36: abstrações de coordenação — contadores TTL, bus pub/sub,
/// gate de provider e fan-out cross-réplica do event stream.
/// </summary>
public class Spec36CoordinationTests
{
    [Fact]
    public async Task InMemoryCounter_Increments_WithTtl()
    {
        var c = new InMemoryCounter();
        (await c.IncrementAsync("k", TimeSpan.FromMinutes(1), default))
            .ShouldBe(1);
        (await c.IncrementAsync("k", TimeSpan.FromMinutes(1), default))
            .ShouldBe(2);
        (await c.IncrementAsync("other", TimeSpan.FromMinutes(1), default))
            .ShouldBe(1);
    }

    [Fact]
    public async Task InMemoryCounter_Expired_Resets()
    {
        var c = new InMemoryCounter();
        await c.IncrementAsync("k", TimeSpan.FromMilliseconds(1), default);
        await Task.Delay(20);
        (await c.IncrementAsync("k", TimeSpan.FromMinutes(1), default))
            .ShouldBe(1);
    }

    [Fact]
    public async Task InMemoryBus_PubSub_RoundTrips_Payload()
    {
        var bus = new InMemoryCoordinationBus(
            NullLogger<InMemoryCoordinationBus>.Instance);
        var received = new TaskCompletionSource<string>();
        await bus.SubscribeAsync("ch",
            (json, _) => { received.TrySetResult(json); return Task.CompletedTask; },
            default);
        await bus.PublishAsync("ch", new { hello = "world" }, default);
        (await received.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .ShouldContain("world");
    }

    [Fact]
    public void ConfigGate_MultiReplica_WithoutRedis_Throws()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Deployment:Replicas"] = "2",
                ["Cache:Provider"] = "memory",
            }).Build();
        Should.Throw<InvalidOperationException>(
            () => new Microsoft.Extensions.DependencyInjection.ServiceCollection()
                .AddCoordinationCache(cfg));
    }

    [Fact]
    public void ConfigGate_Redis_WithoutConnString_Throws()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Cache:Provider"] = "redis",
            }).Build();
        Should.Throw<InvalidOperationException>(
            () => new Microsoft.Extensions.DependencyInjection.ServiceCollection()
                .AddCoordinationCache(cfg));
    }

    [Fact]
    public void ConfigGate_SingleReplica_Memory_Ok()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Deployment:Replicas"] = "1",
            }).Build();
        new ServiceCollection()
            .AddLogging().AddCoordinationCache(cfg);
    }

    [Fact]
    public async Task NullLock_Always_Acquires()
    {
        var l = new NullDistributedLock();
        await using var h = await l.TryAcquireAsync(
            "x", TimeSpan.FromSeconds(1), default);
        h.ShouldNotBeNull();
        h.IsHeld.ShouldBeTrue();
    }

    [Fact]
    public async Task DistributedEventBus_Remote_Publish_Reaches_Subscriber()
    {
        // Duas "réplicas" compartilhando o mesmo bus de coordenação:
        // publish no bus B deve chegar ao subscriber do bus A.
        var coord = new InMemoryCoordinationBus(
            NullLogger<InMemoryCoordinationBus>.Instance);
        var conv = Guid.NewGuid();
        var localA = new InMemoryEventBus();
        var busA = new DistributedEventBus(localA, coord,
            NullLogger<DistributedEventBus>.Instance);
        var localB = new InMemoryEventBus();
        var busB = new DistributedEventBus(localB, coord,
            NullLogger<DistributedEventBus>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var read = Task.Run(async () =>
        {
            await foreach (var e in busA.SubscribeAsync(conv, cts.Token))
            {
                return e;
            }

            return null;
        });
        await Task.Delay(150); // garante subscribe+watch antes do publish

        var evt = new MessageEvent
        {
            LlmMessage = System.Text.Json.JsonSerializer
                .SerializeToElement(new { role = "assistant" }),
        };
        await busB.PublishAsync(conv, evt, cts.Token);

        var got = await read;
        got.ShouldBeOfType<MessageEvent>();
        got!.Id.ShouldBe(evt.Id);
    }

    [Fact]
    public async Task DistributedEventBus_Own_Publish_Not_Echoed()
    {
        // Redis pub/sub entrega ao próprio publisher — o filtro por
        // origin impede o evento aparecer duplicado no stream local.
        var coord = new InMemoryCoordinationBus(
            NullLogger<InMemoryCoordinationBus>.Instance);
        var conv = Guid.NewGuid();
        var localA = new InMemoryEventBus();
        var busA = new DistributedEventBus(localA, coord,
            NullLogger<DistributedEventBus>.Instance);

        var seen = new List<EventBase>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = Task.Run(async () =>
        {
            await foreach (var e in busA.SubscribeAsync(conv, cts.Token))
            {
                seen.Add(e);
                if (seen.Count >= 2)
                {
                    return;
                }
            }
        });
        await Task.Delay(150);
        await busA.PublishAsync(conv, new MessageEvent
        {
            LlmMessage = System.Text.Json.JsonSerializer
                .SerializeToElement(new { role = "assistant" }),
        }, cts.Token);
        await Task.Delay(300); // janela p/ um eco hipotético chegar
        seen.ShouldHaveSingleItem();
        _ = read;
    }

    [Fact]
    public async Task DistributedEventBus_Global_Stays_Local_Only()
    {
        // Evento remoto injetado via PublishLocalAsync nunca entra no
        // stream global — webhook dispara só no nó de origem.
        var coord = new InMemoryCoordinationBus(
            NullLogger<InMemoryCoordinationBus>.Instance);
        var conv = Guid.NewGuid();
        var localA = new InMemoryEventBus();
        var busA = new DistributedEventBus(localA, coord,
            NullLogger<DistributedEventBus>.Instance);
        var busB = new DistributedEventBus(new InMemoryEventBus(), coord,
            NullLogger<DistributedEventBus>.Instance);

        var globalA = new List<EventEnvelope>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var collect = Task.Run(async () =>
        {
            await foreach (var e in localA.SubscribeAllAsync(cts.Token))
            {
                globalA.Add(e);
            }
        });
        var read = Task.Run(async () =>
        {
            await foreach (var e in busA.SubscribeAsync(conv, cts.Token))
            {
                return e;
            }

            return null;
        });
        await Task.Delay(150);
        await busB.PublishAsync(conv, new MessageEvent
        {
            LlmMessage = System.Text.Json.JsonSerializer
                .SerializeToElement(new { role = "assistant" }),
        }, cts.Token);
        (await read).ShouldNotBeNull();
        await Task.Delay(200);
        globalA.ShouldBeEmpty();
        _ = collect;
    }

    [Fact]
    public async Task DistributedEventBus_Remote_OutOfOrder_Reorders_BySeq()
    {
        // Pub/sub pode invocar handlers fora de ordem/paralelo — o seq do
        // publisher reordena: seq=1 chega primeiro, fica em buffer e só
        // emite depois que seq=0 chega.
        var coord = new InMemoryCoordinationBus(
            NullLogger<InMemoryCoordinationBus>.Instance);
        var conv = Guid.NewGuid();
        var busA = new DistributedEventBus(new InMemoryEventBus(), coord,
            NullLogger<DistributedEventBus>.Instance);

        var e0 = new MessageEvent
        {
            LlmMessage = System.Text.Json.JsonSerializer.SerializeToElement(
                new { role = "user", text = "first" }),
        };
        var e1 = new MessageEvent
        {
            LlmMessage = System.Text.Json.JsonSerializer.SerializeToElement(
                new { role = "user", text = "second" }),
        };

        var received = new List<string>();
        var tcs = new TaskCompletionSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var read = Task.Run(async () =>
        {
            await foreach (var e in busA.SubscribeAsync(conv, cts.Token))
            {
                received.Add(e.Id);
                if (received.Count == 2)
                {
                    tcs.TrySetResult();
                }
            }
        });
        await Task.Delay(150); // garante subscribe+watch antes do publish

        foreach (var (seq, e) in new[] { (1L, e1), (0L, e0) })
        {
            await coord.PublishAsync($"events:{conv}", new
            {
                origin = "remote-node",
                conversationId = conv,
                seq,
                @event = System.Text.Json.JsonSerializer.Serialize(
                    e, typeof(EventBase), EventJson.Options),
            }, cts.Token);
        }

        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        done.ShouldBe(tcs.Task, "reordered events never emitted");
        received.ShouldBe([e0.Id, e1.Id]);
        _ = read;
    }
}
