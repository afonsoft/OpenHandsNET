using System.Text.Json;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Coordination;
using OpenHandsNET.Application.Identity;

using StackExchange.Redis;

namespace OpenHandsNET.Infrastructure.Cache;

/// <summary>SPEC-36 RF-001: opções de cache/coordenação distribuída.</summary>
public sealed class CacheOptions
{
    /// <summary><c>memory</c> (default) | <c>redis</c> (exigido se Replicas&gt;1).</summary>
    public string Provider { get; set; } = "memory";

    /// <summary>Conn string redis:// ou rediss:// (TLS) — só via env/secret.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Prefixo de todas as keys (<c>ohn:</c> por default).</summary>
    public string InstanceName { get; set; } = "ohn:";
}

/// <summary>SPEC-36: cache/coordenação — fail-open, degrades a local.</summary>
public static class CacheRegistration
{
    /// <summary>True quando multi-réplica exige provider redis.</summary>
    public static bool RequiresRedis(IConfiguration cfg) =>
        int.TryParse(cfg["Deployment:Replicas"], out var n) && n > 1;

    /// <summary>True quando <c>Cache:Provider=redis</c>.</summary>
    public static bool RedisEnabled(IConfiguration cfg) =>
        cfg["Cache:Provider"]?.Equals("redis",
            StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// RF-001/004: valida o gate (Replicas&gt;1 exige redis + conn string) e
    /// registra <see cref="IDistributedCache"/>, <see cref="ICoordinationBus"/>,
    /// <see cref="IDistributedCounter"/>, <see cref="IDistributedLock"/> e
    /// <see cref="IUsageBuffer"/> — Redis ou in-memory/no-op.
    /// </summary>
    public static IServiceCollection AddCoordinationCache(
        this IServiceCollection services, IConfiguration configuration)
    {
        var conn = configuration["Cache:Redis:ConnectionString"];
        if (RequiresRedis(configuration) && !RedisEnabled(configuration))
        {
            throw new InvalidOperationException(
                "Deployment:Replicas > 1 exige Cache:Provider=redis " +
                "(Cache:Redis:ConnectionString) — estado compartilhado " +
                "(warm pool, rate limit, SignalR backplane) não é " +
                "local-only.");
        }

        services.Configure<CacheOptions>(
            configuration.GetSection("Cache"));
        var options = new CacheOptions
        {
            Provider = configuration["Cache:Provider"] ?? "memory",
            ConnectionString = conn,
            InstanceName = configuration["Cache:Redis:InstanceName"] ?? "ohn:",
        };

        if (RedisEnabled(configuration))
        {
            if (string.IsNullOrEmpty(conn))
            {
                throw new InvalidOperationException(
                    "Cache:Provider=redis sem Cache:Redis:ConnectionString");
            }

            // RF-004: fail-open — AbortOnConnectFail=false; os impls
            // degradam p/ local com warning quando o multiplexer cai.
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var co = ConfigurationOptions.Parse(conn!);
                co.AbortOnConnectFail = false;
                return ConnectionMultiplexer.Connect(co);
            });
            services.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = conn;
                o.InstanceName = options.InstanceName;
            });
            services.AddSingleton<ICoordinationBus, RedisCoordinationBus>();
            services.AddSingleton<IDistributedCounter, RedisDistributedCounter>();
            services.AddSingleton<IDistributedLock, RedisDistributedLock>();
            services.AddSingleton<IUsageBuffer, RedisUsageBuffer>();
        }
        else
        {
            services.AddDistributedMemoryCache();
            services.AddSingleton<ICoordinationBus, InMemoryCoordinationBus>();
            services.AddSingleton<IDistributedCounter, InMemoryCounter>();
            services.AddSingleton<IDistributedLock, NullDistributedLock>();
            services.AddSingleton<IUsageBuffer, NullUsageBuffer>();
        }

        return services;
    }
}

/// <summary>Redis pub/sub sobre <c>{instance}bus:{channel}</c>.</summary>
public sealed class RedisCoordinationBus(
    IConnectionMultiplexer mux, Microsoft.Extensions.Options
        .IOptions<CacheOptions> options,
    ILogger<RedisCoordinationBus> logger) : ICoordinationBus
{
    /// <inheritdoc />
    public async Task PublishAsync(
        string channel, object payload, CancellationToken ct)
    {
        try
        {
            await mux.GetSubscriber().PublishAsync(
                RedisChannel.Literal($"{options.Value.InstanceName}bus:{channel}"),
                JsonSerializer.Serialize(payload));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // RF-004: fail-open — publica nada mas não derruba request.
            logger.LogWarning(e, "redis pub failed on {Channel}", channel);
        }
    }

    /// <inheritdoc />
    public async Task SubscribeAsync(string channel,
        Func<string, CancellationToken, Task> handler, CancellationToken ct)
    {
        var sub = mux.GetSubscriber();
        await sub.SubscribeAsync(
            RedisChannel.Literal($"{options.Value.InstanceName}bus:{channel}"),
            async (_, v) =>
            {
                try
                {
                    await handler(v.ToString(), ct);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "redis sub handler on {Channel}",
                        channel);
                }
            });
    }
}

/// <summary>INCR + PEXPIRE — sliding window para rate limit/usage.</summary>
public sealed class RedisDistributedCounter(
    IConnectionMultiplexer mux,
    Microsoft.Extensions.Options.IOptions<CacheOptions> options,
    ILogger<RedisDistributedCounter> logger) : IDistributedCounter
{
    /// <inheritdoc />
    public async Task<long> IncrementAsync(
        string key, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            var db = mux.GetDatabase();
            var k = $"{options.Value.InstanceName}counter:{key}";
            var n = await db.StringIncrementAsync(k);
            if (n == 1)
            {
                await db.KeyExpireAsync(k, ttl);
            }

            return n;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // RF-004: fail-open — retorna 0 (caller decide degrade).
            logger.LogWarning(e, "redis counter {Key} unavailable", key);
            return 0;
        }
    }
}

/// <summary>Single-replica: channels in-process.</summary>
public sealed class InMemoryCoordinationBus(
    ILogger<InMemoryCoordinationBus> logger) : ICoordinationBus
{
    private readonly Dictionary<string,
        List<Func<string, CancellationToken, Task>>> _subs = new();
    private readonly object _lock = new();

    /// <inheritdoc />
    public async Task PublishAsync(
        string channel, object payload, CancellationToken ct)
    {
        List<Func<string, CancellationToken, Task>> handlers;
        var json = JsonSerializer.Serialize(payload);
        lock (_lock)
        {
            handlers = _subs.TryGetValue(channel, out var h)
                ? [.. h] : [];
        }

        foreach (var h in handlers)
        {
            try
            {
                await h(json, ct);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "bus handler on {Channel}", channel);
            }
        }
    }

    /// <inheritdoc />
    public Task SubscribeAsync(string channel,
        Func<string, CancellationToken, Task> handler, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_subs.TryGetValue(channel, out var h))
            {
                h = _subs[channel] = [];
            }

            h.Add(handler);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Single-replica: contadores em memória com TTL.</summary>
public sealed class InMemoryCounter : IDistributedCounter
{
    private readonly Dictionary<string, (long Value, DateTimeOffset Exp)> _c
        = new();
    private readonly object _lock = new();

    /// <inheritdoc />
    public Task<long> IncrementAsync(
        string key, TimeSpan ttl, CancellationToken ct)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (!_c.TryGetValue(key, out var e) || e.Exp < now)
            {
                e = (0, now + ttl);
            }

            _c[key] = (e.Value + 1, e.Exp);
            return Task.FromResult(e.Value + 1);
        }
    }
}