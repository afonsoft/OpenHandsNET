namespace OpenHandsNET.Application.Coordination;

/// <summary>
/// SPEC-36 RF-003: pub/sub tipado. In-memory em single-replica; Redis
/// pub/sub <c>{instance}bus:{channel}</c> quando distribuído.
/// </summary>
public interface ICoordinationBus
{
    /// <summary>Publica payload JSON num canal.</summary>
    Task PublishAsync(string channel, object payload, CancellationToken ct);

    /// <summary>Assina um canal; handler roda por mensagem.</summary>
    Task SubscribeAsync(string channel,
        Func<string, CancellationToken, Task> handler, CancellationToken ct);
}