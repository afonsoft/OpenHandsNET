using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Coordination;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Infrastructure.Eventing;

/// <summary>
/// SPEC-36 RF-002: fan-out cross-réplica do event stream. Publica local
/// (in-memory, ordem preservada) + canal <c>bus:events:{convId}</c> via
/// <see cref="ICoordinationBus"/>; mensagens remotas são injetadas no
/// canal local da conversa (<see cref="InMemoryEventBus.PublishLocalAsync"/>)
/// — sem re-publicar ao bus (sem loop) e sem entrar no stream global
/// (webhook/EventDispatcher continua single-fire no nó de origem).
/// </summary>
public sealed class DistributedEventBus(
    InMemoryEventBus local,
    ICoordinationBus bus,
    ILogger<DistributedEventBus> logger) : IEventBus
{
    private static readonly JsonSerializerOptions Json = EventJson.Options;
    // Envelope do bus usa Web (camelCase) — EventJson snake_case só vale
    // para o payload do evento em si.
    private static readonly JsonSerializerOptions Envelope = new(
        JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, byte> _watched = new();
    private readonly string _origin = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public async ValueTask PublishAsync(
        Guid conversationId, EventBase e, CancellationToken ct)
    {
        await local.PublishAsync(conversationId, e, ct);
        await bus.PublishAsync($"events:{conversationId}", new Wire(
            _origin, conversationId,
            JsonSerializer.Serialize(e, typeof(EventBase), Json)), ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EventBase> SubscribeAsync(
        Guid conversationId, [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureWatched(conversationId, ct);
        await foreach (var e in local.SubscribeAsync(conversationId, ct))
        {
            yield return e;
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<EventEnvelope> SubscribeAllAsync(
        CancellationToken ct) =>
        // Global segue local-only: webhooks/dispatcher disparam uma vez,
        // no nó que produziu o evento (semantic de hoje preservada).
        local.SubscribeAllAsync(ct);

    private void EnsureWatched(Guid conversationId, CancellationToken ct)
    {
        if (!_watched.TryAdd(conversationId, 0))
        {
            return;
        }

        _ = bus.SubscribeAsync($"events:{conversationId}",
            async (json, t) =>
            {
                try
                {
                    var wire = JsonSerializer.Deserialize<Wire>(json, Envelope);
                    // Pub/sub ecoa p/ o próprio publisher — dedup por origin.
                    if (wire is null || wire.Origin == _origin)
                    {
                        return;
                    }

                    if (wire.Event is { } payload)
                    {
                        var e = JsonSerializer.Deserialize<EventBase>(
                            payload, Json);
                        if (e is not null)
                        {
                            await local.PublishLocalAsync(
                                wire.ConversationId, e, t);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "remote event on {Conv} dropped", conversationId);
                }
            }, ct);
    }

    private sealed record Wire(string Origin, Guid ConversationId, string Event);
}
