using System.Text;
using OpenHandsNET.Application.Events;

namespace OpenHandsNET.Infrastructure.Agent.Deltas;

/// <summary>
/// SPEC-34 RF-001: accumulates LLM text deltas into <see cref="DeltaFrame"/>s
/// published on the activity bus. Flushes when the buffer reaches 64 bytes
/// or 16ms elapsed since the last flush (evaluated on each Add — deltas
/// arrive continuously so no timer is needed).
/// </summary>
public sealed class DeltaBatcher(
    Guid conversationId, string itemId, IAgentActivityBus bus)
{
    private const int FlushBytes = 64;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(16);

    private readonly StringBuilder _buf = new();
    private long _seq;
    private DateTimeOffset _lastFlush = DateTimeOffset.UtcNow;

    /// <summary>Buffers a delta; flushes on size/age thresholds.</summary>
    public void Add(string text)
    {
        _buf.Append(text);
        if (_buf.Length >= FlushBytes
            || DateTimeOffset.UtcNow - _lastFlush >= FlushInterval)
        {
            Flush();
        }
    }

    /// <summary>Publishes any pending text (call before the durable event).</summary>
    public void Flush()
    {
        if (_buf.Length == 0)
        {
            return;
        }

        bus.Publish(conversationId,
            new DeltaFrame(itemId, _seq++, _buf.ToString()));
        _buf.Clear();
        _lastFlush = DateTimeOffset.UtcNow;
    }
}
