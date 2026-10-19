using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Identity;

using StackExchange.Redis;

namespace OpenHandsNET.Infrastructure.Cache;

/// <summary>
/// SPEC-36 RF-002: usage write-path bufferado —
/// <c>{inst}usage:{org}:{period}</c> hash <c>{tokens, cost_cents}</c> +
/// set dirty <c>{inst}usage:dirty</c>. Add/SADD é atômico via Lua (um
/// incremento concorrente ao confirm nunca perde a marca de dirty).
/// O confirm decrementa e só limpa quando o hash zera — deltas que
/// chegaram entre snapshot e confirmação ficam p/ a próxima rodada.
/// </summary>
public sealed class RedisUsageBuffer(
    IConnectionMultiplexer mux,
    IOptions<CacheOptions> options,
    ILogger<RedisUsageBuffer> logger) : IUsageBuffer
{
    private static readonly LuaScript AddScript = LuaScript.Prepare(
        "redis.call('HINCRBY', @key, 'tokens', @tokens); " +
        "redis.call('HINCRBY', @key, 'cost_cents', @cost); " +
        "redis.call('PEXPIRE', @key, @ttl); " +
        "redis.call('SADD', @dirty, @member); return 1");

    private static readonly LuaScript ConfirmScript = LuaScript.Prepare(
        "local t = redis.call('HINCRBY', @key, 'tokens', @negTokens); " +
        "local c = redis.call('HINCRBY', @key, 'cost_cents', @negCost); " +
        "if t <= 0 and c <= 0 then " +
        "redis.call('DEL', @key); " +
        "redis.call('SREM', @dirty, @member); end return t");

    private static readonly TimeSpan KeyTtl = TimeSpan.FromDays(40);

    private string Key(Guid org, string period) =>
        $"{options.Value.InstanceName}usage:{org}:{period}";

    private string DirtyKey => $"{options.Value.InstanceName}usage:dirty";

    /// <inheritdoc />
    public async Task AddAsync(
        Guid orgId, long tokens, decimal? costUsd, CancellationToken ct)
    {
        try
        {
            var period = Period();
            await mux.GetDatabase().ScriptEvaluateAsync(AddScript, new
            {
                key = (RedisKey)Key(orgId, period),
                dirty = (RedisKey)DirtyKey,
                tokens = Math.Max(0, tokens),
                cost = ToCents(costUsd),
                ttl = (long)KeyTtl.TotalMilliseconds,
                member = (RedisValue)Member(orgId, period),
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // RF-004: fail-open — o caller decide (EF segue fonte).
            logger.LogWarning(e, "usage buffer add failed {Org}", orgId);
        }
    }

    /// <inheritdoc />
    public async Task<PendingUsage> GetPendingAsync(
        Guid orgId, string period, CancellationToken ct)
    {
        try
        {
            var e = await mux.GetDatabase()
                .HashGetAllAsync(Key(orgId, period));
            return new PendingUsage(orgId, period,
                Field(e, "tokens"), FromCents(Field(e, "cost_cents")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "usage buffer read failed {Org}", orgId);
            return new PendingUsage(orgId, period, 0, 0m);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingUsage>> SnapshotPendingAsync(
        CancellationToken ct)
    {
        try
        {
            var db = mux.GetDatabase();
            var members = await db.SetMembersAsync(DirtyKey);
            var list = new List<PendingUsage>(members.Length);
            foreach (var m in members)
            {
                // member = "{org}:{period}" — período junto p/ evitar SCAN
                var s = m.ToString();
                var sep = s.IndexOf(':');
                if (sep <= 0 || !Guid.TryParse(s[..sep], out var org))
                {
                    continue;
                }

                var period = s[(sep + 1)..];
                var e = await db.HashGetAllAsync(Key(org, period));
                var tokens = Field(e, "tokens");
                var cost = FromCents(Field(e, "cost_cents"));
                if (tokens > 0 || cost > 0)
                {
                    list.Add(new PendingUsage(org, period, tokens, cost));
                }
            }

            return list;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "usage buffer snapshot failed");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task ConfirmAsync(PendingUsage drained, CancellationToken ct)
    {
        try
        {
            await mux.GetDatabase().ScriptEvaluateAsync(ConfirmScript, new
            {
                key = (RedisKey)Key(drained.OrgId, drained.Period),
                dirty = (RedisKey)DirtyKey,
                negTokens = -drained.Tokens,
                negCost = -ToCents(drained.CostUsd),
                member = (RedisValue)Member(drained.OrgId, drained.Period),
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "usage buffer confirm failed {Org}",
                drained.OrgId);
        }
    }

    private static string Member(Guid org, string period) =>
        $"{org}:{period}";

    private static string Period() => DateTimeOffset.UtcNow.ToString("yyyy-MM");

    private static long Field(HashEntry[] e, string name) =>
        e.FirstOrDefault(h => h.Name == name).Value.TryParse(out long v)
            ? v : 0;

    private static long ToCents(decimal? usd) =>
        usd is { } c ? (long)Math.Round(c * 100m) : 0;

    private static decimal FromCents(long cents) => cents / 100m;
}