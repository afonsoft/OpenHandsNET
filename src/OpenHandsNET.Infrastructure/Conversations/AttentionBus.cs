using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using OpenHandsNET.Application.Conversations;

namespace OpenHandsNET.Infrastructure.Conversations;

/// <summary>
/// SPEC-44 RF-003: fan-out in-memory — um canal por assinante; ticks
/// descartados quando ninguém escuta (mesmo padrão do
/// <c>AgentActivityBus</c>).
/// </summary>
public sealed class AttentionBus : IAttentionBus
{
    private readonly ConcurrentDictionary<Guid, Channel<DateTimeOffset>>
        _subs = new();

    /// <inheritdoc />
    public void PublishChanged()
    {
        var tick = DateTimeOffset.UtcNow;
        foreach (var ch in _subs.Values)
        {
            ch.Writer.TryWrite(tick);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DateTimeOffset> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var ch = Channel.CreateUnbounded<DateTimeOffset>();
        var key = Guid.NewGuid();
        _subs[key] = ch;
        try
        {
            await foreach (var t in ch.Reader.ReadAllAsync(ct))
            {
                yield return t;
            }
        }
        finally
        {
            _subs.TryRemove(key, out _);
        }
    }
}