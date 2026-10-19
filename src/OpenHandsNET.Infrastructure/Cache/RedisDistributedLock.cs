using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Coordination;

using StackExchange.Redis;

namespace OpenHandsNET.Infrastructure.Cache;

/// <summary>
/// SPEC-36 RF-002: lock distribuído <c>SET {inst}lock:{name} token NX PX</c>
/// com renovação em background (ttl/3) e release compare-token via Lua.
/// RF-004: falha de coordenação no acquire degrada fail-open (concede
/// handle local) com warning — o host nunca morre por Redis down.
/// </summary>
public sealed class RedisDistributedLock(
    IConnectionMultiplexer mux,
    IOptions<CacheOptions> options,
    ILogger<RedisDistributedLock> logger) : IDistributedLock
{
    private static readonly LuaScript ReleaseScript = LuaScript.Prepare(
        "if redis.call('GET', @key) == @token " +
        "then return redis.call('DEL', @key) else return 0 end");

    /// <inheritdoc />
    public async Task<IDistributedLockHandle?> TryAcquireAsync(
        string name, TimeSpan ttl, CancellationToken ct)
    {
        var key = $"{options.Value.InstanceName}lock:{name}";
        var token = Guid.NewGuid().ToString("N");
        try
        {
            var db = mux.GetDatabase();
            var ok = await db.StringSetAsync(
                key, token, ttl, When.NotExists);
            if (!ok)
            {
                return null; // outra réplica detém
            }

            return new RedisLockHandle(
                db, key, token, ttl, logger);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // RF-004: Redis down → concede local (degraded p/ single-node).
            logger.LogWarning(e,
                "redis lock {Name} unavailable — concedendo local", name);
            return new RedisLockHandle(
                null, key, token, ttl, logger);
        }
    }

    private sealed class RedisLockHandle : IDistributedLockHandle
    {
        private readonly IDatabase? _db;
        private readonly string _key;
        private readonly string _token;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task? _renew;

        public RedisLockHandle(IDatabase? db, string key, string token,
            TimeSpan ttl, ILogger logger)
        {
            _db = db;
            _key = key;
            _token = token;
            _logger = logger;
            IsHeld = true;
            if (db is not null)
            {
                var interval = TimeSpan.FromMilliseconds(
                    Math.Max(1000, ttl.TotalMilliseconds / 3));
                _renew = RenewLoopAsync(interval, ttl);
            }
        }

        /// <inheritdoc />
        public bool IsHeld { get; private set; }

        private async Task RenewLoopAsync(TimeSpan interval, TimeSpan ttl)
        {
            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    var ok = await _db!.StringSetAsync(
                        _key, _token, ttl, When.Exists);
                    if (!ok)
                    {
                        IsHeld = false; // perdeu o lease
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                // RF-004: renovação falhou por outage → perde o lease
                // (degrada gracioso; outra réplica pode assumir).
                _logger.LogWarning(e, "redis lock renew failed {Key}", _key);
                IsHeld = false;
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            IsHeld = false;
            await _cts.CancelAsync();
            if (_renew is not null)
            {
                try
                {
                    await _renew;
                }
                catch (OperationCanceledException) { }
            }

            if (_db is not null)
            {
                try
                {
                    await _db.ScriptEvaluateAsync(ReleaseScript,
                        new { key = (RedisKey)_key, token = (RedisValue)_token });
                }
                catch (Exception e)
                {
                    _logger.LogDebug(e, "redis lock release {Key}", _key);
                }
            }

            _cts.Dispose();
        }
    }
}