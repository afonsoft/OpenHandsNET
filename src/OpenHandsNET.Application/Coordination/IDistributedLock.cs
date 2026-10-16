namespace OpenHandsNET.Application.Coordination;

/// <summary>
/// SPEC-36 RF-002: lock distribuído (<c>SET NX PX</c> no Redis) para
/// trabalho singleton entre réplicas — warm pool leader, orphan sweep,
/// usage flush. <see cref="NullDistributedLock"/> é o default de
/// single-replica (sempre concede: a gate de <c>Deployment:Replicas</c>
/// garante que memory = 1 nó).
/// </summary>
public interface IDistributedLock
{
    /// <summary>
    /// Tenta adquirir <paramref name="name"/> por <paramref name="ttl"/>.
    /// Null = outro nó detém. Em falha de coordenação o impl degrada
    /// fail-open (concede local) com warning — nunca bloqueia o host.
    /// </summary>
    Task<IDistributedLockHandle?> TryAcquireAsync(
        string name, TimeSpan ttl, CancellationToken ct);
}

/// <summary>Handle de lease; renova em background até dispose.</summary>
public interface IDistributedLockHandle : IAsyncDisposable
{
    /// <summary>False quando a renovação falhou (outro nó pode assumir).</summary>
    bool IsHeld { get; }
}

/// <summary>Single-replica: sempre concede (a gate impede multi-nó em memory).</summary>
public sealed class NullDistributedLock : IDistributedLock
{
    /// <inheritdoc />
    public Task<IDistributedLockHandle?> TryAcquireAsync(
        string name, TimeSpan ttl, CancellationToken ct) =>
        Task.FromResult<IDistributedLockHandle?>(new Handle());

    private sealed class Handle : IDistributedLockHandle
    {
        /// <inheritdoc />
        public bool IsHeld => true;

        /// <inheritdoc />
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
