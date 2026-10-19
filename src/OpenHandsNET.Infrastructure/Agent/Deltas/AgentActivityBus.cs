using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using OpenHandsNET.Application.Events;

namespace OpenHandsNET.Infrastructure.Agent.Deltas;

/// <summary>
/// SPEC-34 RF-001: in-memory activity bus — one unbounded channel per
/// subscriber keyed to its conversation; frames never persisted and
/// dropped when nobody listens.
/// </summary>
public sealed class AgentActivityBus : IAgentActivityBus
{
    private readonly ConcurrentDictionary<Guid,
        (Guid ConversationId, Channel<DeltaFrame> Channel)> _subs = new();

    /// <inheritdoc />
    public void Publish(Guid conversationId, DeltaFrame frame)
    {
        foreach (var sub in _subs.Values)
        {
            if (sub.ConversationId == conversationId)
            {
                sub.Channel.Writer.TryWrite(frame);
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DeltaFrame> SubscribeAsync(
        Guid conversationId, [EnumeratorCancellation] CancellationToken ct)
    {
        var ch = Channel.CreateUnbounded<DeltaFrame>();
        var key = Guid.NewGuid();
        _subs[key] = (conversationId, ch);
        try
        {
            await foreach (var f in ch.Reader.ReadAllAsync(ct))
            {
                yield return f;
            }
        }
        finally
        {
            _subs.TryRemove(key, out _);
        }
    }
}