using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Infrastructure.Eventing;

/// <summary>
/// In-process event fan-out: one unbounded channel per conversation
/// (live events; persistence/replay go through <see cref="IEventService"/>).
/// </summary>
public sealed class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Guid, Channel<EventBase>> _channels = new();
    private readonly Channel<EventEnvelope> _global =
        System.Threading.Channels.Channel.CreateUnbounded<EventEnvelope>();

    /// <inheritdoc />
    public async ValueTask PublishAsync(Guid conversationId, EventBase e, CancellationToken ct)
    {
        var channel = Channel(conversationId);
        await channel.Writer.WriteAsync(e, ct);
        await _global.Writer.WriteAsync(new EventEnvelope(conversationId, e), ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EventBase> SubscribeAsync(
        Guid conversationId, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel(conversationId);
        await foreach (var e in channel.Reader.ReadAllAsync(ct))
        {
            yield return e;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EventEnvelope> SubscribeAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var e in _global.Reader.ReadAllAsync(ct))
        {
            yield return e;
        }
    }

    private Channel<EventBase> Channel(Guid conversationId) =>
        _channels.GetOrAdd(conversationId, static _ =>
            System.Threading.Channels.Channel.CreateUnbounded<EventBase>(
                new UnboundedChannelOptions { SingleReader = false, SingleWriter = false }));
}
