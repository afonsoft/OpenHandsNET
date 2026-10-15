using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Coordination;
using OpenHandsNET.Application.Events;

namespace OpenHandsNET.Infrastructure.Agent.Deltas;

/// <summary>
/// SPEC-36 RF-002: <see cref="IAgentActivityBus"/> cross-réplica — deltas
/// efêmeros publicados num nó chegam a subscribers nos outros via
/// <c>bus:activity:{convId}</c>. Frames remotos entram direto no canal
/// local do subscriber; filtro <c>origin</c> evita o eco do pub/sub
/// (Redis entrega ao próprio publisher) — nada é re-publicado ao bus.
/// </summary>
public sealed class DistributedAgentActivityBus(
    AgentActivityBus local,
    ICoordinationBus bus,
    ILogger<DistributedAgentActivityBus> logger) : IAgentActivityBus
{
    private static readonly JsonSerializerOptions Json = new(
        JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, Channel<DeltaFrame>> _remote = new();
    private readonly ConcurrentDictionary<Guid, byte> _watched = new();
    private readonly string _origin = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public void Publish(Guid conversationId, DeltaFrame frame)
    {
        local.Publish(conversationId, frame);
        _ = bus.PublishAsync($"activity:{conversationId}",
            new Wire(_origin, frame), CancellationToken.None);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DeltaFrame> SubscribeAsync(
        Guid conversationId, [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureWatched(conversationId);
        var ch = _remote.GetOrAdd(conversationId,
            _ => Channel.CreateUnbounded<DeltaFrame>());

        // Merge: frames locais + remotos convergem no mesmo yield.
        var output = Channel.CreateUnbounded<DeltaFrame>();
        var pumpLocal = Pump(local.SubscribeAsync(conversationId, ct),
            output.Writer, ct);
        var pumpRemote = Pump(ch.Reader.ReadAllAsync(ct),
            output.Writer, ct);
        try
        {
            await foreach (var f in output.Reader.ReadAllAsync(ct))
            {
                yield return f;
            }
        }
        finally
        {
            output.Writer.TryComplete();
            await Task.WhenAll(
                Swallow(pumpLocal), Swallow(pumpRemote));
        }

        static async Task Pump(IAsyncEnumerable<DeltaFrame> src,
            ChannelWriter<DeltaFrame> dst, CancellationToken ct)
        {
            await foreach (var f in src.WithCancellation(ct))
            {
                dst.TryWrite(f);
            }
        }

        static async Task Swallow(Task t)
        {
            try
            {
                await t;
            }
            catch (OperationCanceledException) { }
        }
    }

    private void EnsureWatched(Guid conversationId)
    {
        if (!_watched.TryAdd(conversationId, 0))
        {
            return;
        }

        _ = bus.SubscribeAsync($"activity:{conversationId}",
            (json, t) =>
            {
                try
                {
                    var w = JsonSerializer.Deserialize<Wire>(json, Json);
                    if (w is not null && w.Origin != _origin)
                    {
                        _remote.GetOrAdd(conversationId,
                                _ => Channel.CreateUnbounded<DeltaFrame>())
                            .Writer.TryWrite(w.Frame);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e,
                        "remote delta on {Conv} dropped", conversationId);
                }

                return Task.CompletedTask;
            }, CancellationToken.None);
    }

    private sealed record Wire(string Origin, DeltaFrame Frame);
}
