using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Coordination;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Infrastructure.Cache;

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
    private readonly ConcurrentDictionary<Guid, long> _seq = new();
    private readonly ConcurrentDictionary<Guid, SeqReorderBuffer<DeltaFrame>> _ingress = new();
    private readonly string _origin = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public void Publish(Guid conversationId, DeltaFrame frame)
    {
        local.Publish(conversationId, frame);
        var seq = _seq.AddOrUpdate(conversationId, 0L, static (_, s) => s + 1);
        _ = bus.PublishAsync($"activity:{conversationId}",
            new Wire(_origin, seq, frame), CancellationToken.None);
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
            (json, t) => OnRemoteAsync(conversationId, json, t), CancellationToken.None);
    }

    /// <summary>
    /// Handlers do pub/sub podem rodar em paralelo — injeção remota é
    /// serializada por conversa e reordenada pelo <c>seq</c> do publisher,
    /// senão deltas adjacentes podem entrar invertidos no canal local.
    /// </summary>
    private async Task OnRemoteAsync(
        Guid conversationId, string json, CancellationToken t)
    {
        try
        {
            var w = JsonSerializer.Deserialize<Wire>(json, Json);
            if (w is null || w.Origin == _origin)
            {
                return;
            }

            if (w.Seq is not { } seq)
            {
                // Réplica antiga (sem seq): entrega direta, sem reorder.
                _remote.GetOrAdd(conversationId,
                    static _ => Channel.CreateUnbounded<DeltaFrame>())
                    .Writer.TryWrite(w.Frame);
                return;
            }

            var ingress = _ingress.GetOrAdd(conversationId, id =>
                new SeqReorderBuffer<DeltaFrame>((f, _) =>
                {
                    _remote.GetOrAdd(id,
                        static _ => Channel.CreateUnbounded<DeltaFrame>())
                        .Writer.TryWrite(f);
                    return Task.CompletedTask;
                }));
            await ingress.EnqueueAsync(seq, w.Frame, t);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e,
                "remote delta on {Conv} dropped", conversationId);
        }
    }

    private sealed record Wire(string Origin, long? Seq, DeltaFrame Frame);
}
