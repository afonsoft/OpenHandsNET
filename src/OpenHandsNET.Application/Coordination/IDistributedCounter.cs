namespace OpenHandsNET.Application.Coordination;

/// <summary>
/// SPEC-36 RF-003: contadores com TTL — rate limit sliding window e
/// usage buffer (<c>ohn:counter:{key}</c>, INCR + expire).
/// </summary>
public interface IDistributedCounter
{
    /// <summary>Incrementa e devolve o valor corrente (janela <paramref name="ttl"/>).</summary>
    Task<long> IncrementAsync(string key, TimeSpan ttl, CancellationToken ct);
}