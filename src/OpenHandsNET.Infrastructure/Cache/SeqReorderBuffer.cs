namespace OpenHandsNET.Infrastructure.Cache;

/// <summary>
/// SPEC-36: buffer de reordenação de ingresso remoto. O pub/sub pode
/// invocar handlers em paralelo e fora de ordem — cada item remoto
/// carrega um <c>seq</c> monotônico (por conversa, do publisher) e aqui
/// sai na ordem do publisher:
/// <list type="bullet">
/// <item>seq contíguo ou atrasado (<c>&lt;= next</c>) emite na hora —
/// latência zero no caminho ordenado;</item>
/// <item>gap/out-of-order espera a janela <paramref name="holdback"/>
/// (default 50ms) para o predecessor chegar; expirada, salta o gap —
/// entrega garantida mesmo com seq perdido;</item>
/// <item>estouro de <see cref="MaxPending"/> também abandona o gap.</item>
/// </list>
/// A emissão é serializada por instância do buffer (um por conversa).
/// </summary>
internal sealed class SeqReorderBuffer<T>(
    Func<T, CancellationToken, Task> emit, TimeSpan? holdback = null)
{
    private const int DefaultHoldbackMs = 50;
    private const int MaxPending = 512;

    private readonly TimeSpan _holdback = holdback ?? TimeSpan.FromMilliseconds(DefaultHoldbackMs);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SortedDictionary<long, (T Item, DateTimeOffset Deadline)> _pending = new();
    private long _next = -1;
    private int _drainScheduled;

    /// <summary>
    /// Enfileira um item remoto. O próximo seq esperado emite na hora;
    /// um gap agenda um dreno adiado para dar chance ao predecessor.
    /// </summary>
    public async ValueTask EnqueueAsync(long seq, T item, CancellationToken ct)
    {
        bool left;
        await _gate.WaitAsync(ct);
        try
        {
            _pending[seq] = (item, DateTimeOffset.UtcNow + _holdback);
            await DrainAsync(ct);
            left = _pending.Count > 0;
        }
        finally
        {
            _gate.Release();
        }

        if (left)
        {
            ScheduleDrain();
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        while (_pending.Count > 0)
        {
            var front = _pending.First();
            var expired = now >= front.Value.Deadline;
            if (front.Key > _next && !expired && _pending.Count <= MaxPending)
            {
                break;
            }

            _pending.Remove(front.Key);
            if (front.Key >= _next)
            {
                _next = front.Key + 1;
            }

            await emit(front.Value.Item, ct);
        }
    }

    private void ScheduleDrain()
    {
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Delay(_holdback).ContinueWith(async _ =>
        {
            Interlocked.Exchange(ref _drainScheduled, 0);
            bool left;
            await _gate.WaitAsync();
            try
            {
                await DrainAsync(CancellationToken.None);
                left = _pending.Count > 0;
            }
            finally
            {
                _gate.Release();
            }

            if (left)
            {
                ScheduleDrain();
            }
        }, TaskScheduler.Default);
    }
}