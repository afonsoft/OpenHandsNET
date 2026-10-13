using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace OpenHandsNET.Api.Hubs;

/// <summary>
/// Terminal bridge MVP: per-conversation fan-out of terminal output to hub
/// subscribers and stdin buffering. The real bash-backed bridge attaches when
/// the agent worker opens an interactive session (SPEC-09); until then input
/// is captured and output is a no-op stream.
/// </summary>
public sealed class TerminalBridgeService
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _out = new();
    private readonly ConcurrentDictionary<Guid, Channel<string>> _in = new();

    /// <summary>Buffer terminal input for later consumption by the worker.</summary>
    public ValueTask WriteAsync(Guid conversationId, string data, CancellationToken ct) =>
        _in.GetOrAdd(conversationId,
            _ => Channel.CreateUnbounded<string>()).Writer.WriteAsync(data, ct);

    /// <summary>Reads buffered terminal input (agent-worker side).</summary>
    public IAsyncEnumerable<string> ReadInputAsync(
        Guid conversationId, CancellationToken ct) =>
        _in.GetOrAdd(conversationId,
            _ => Channel.CreateUnbounded<string>()).Reader.ReadAllAsync(ct);

    /// <summary>Publishes terminal output to hub subscribers.</summary>
    public ValueTask PublishAsync(Guid conversationId, string data, CancellationToken ct) =>
        _out.GetOrAdd(conversationId,
            _ => Channel.CreateUnbounded<string>()).Writer.WriteAsync(data, ct);

    /// <summary>Streams terminal output (hub side).</summary>
    public async IAsyncEnumerable<string> SubscribeAsync(
        Guid conversationId, [EnumeratorCancellation] CancellationToken ct)
    {
        var reader = _out.GetOrAdd(conversationId,
            _ => Channel.CreateUnbounded<string>()).Reader;
        await foreach (var chunk in reader.ReadAllAsync(ct))
        {
            yield return chunk;
        }
    }
}
