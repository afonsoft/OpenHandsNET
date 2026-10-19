namespace OpenHandsNET.Client.Services;

/// <summary>
/// SPEC-44 RF-003: estado scoped do inbox de attention — carrega no boot,
/// invalida via ticks SignalR <c>AttentionChanges</c> (debounce 2s, sem
/// poll) e dispara <see cref="Changed"/> a cada refresh. Itens novos de
/// conversa ≠ aberta viram toast (opt-out via
/// <c>ohn.attention.toasts</c> em localStorage — espelhado em Settings).
/// </summary>
public sealed class AttentionInboxState(
    OpenHandsNetApiClient api, ConversationHubClient hub,
    ToastService toasts, Microsoft.JSInterop.IJSRuntime js)
    : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _watch;
    private int _lastCount;

    /// <summary>Itens do inbox (mais recentes primeiro).</summary>
    public IReadOnlyList<AttentionItemDto> Items { get; private set; } = [];

    /// <summary>Quantidade de conversas aguardando o usuário.</summary>
    public int Count => Items.Count;

    /// <summary>Dispara após cada refresh (count/itens mudaram ou não).</summary>
    public event Action? Changed;

    /// <summary>Preferences opt-out key (SPEC-44 RF-004).</summary>
    public const string ToastOptOutKey = "ohn.attention.toasts";

    /// <summary>Carga inicial + watch de ticks (idempotente).</summary>
    public async Task EnsureStartedAsync()
    {
        if (_watch is not null)
        {
            return;
        }

        await RefreshAsync(notifyNew: false);
        _watch = Task.Run(() => WatchAsync(_cts.Token), _cts.Token);
    }

    /// <summary>Refetch — chamável por componentes após ações locais.</summary>
    public async Task RefreshAsync(bool notifyNew = true)
    {
        try
        {
            var dto = await api.GetAttentionAsync(_cts.Token);
            var prev = _lastCount;
            Items = dto.Items;
            _lastCount = Items.Count;
            Changed?.Invoke();
            if (notifyNew && Items.Count > prev
                && Items.FirstOrDefault() is { } top)
            {
                await MaybeToastAsync(top);
            }
        }
        catch (Exception) { /* offline/transient — mantém o último estado */ }
    }

    /// <summary>Refetch debounced (ticks rápidos colapsam em ~2s).</summary>
    public void Invalidate()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token);
            await RefreshAsync();
        }, _cts.Token);
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        // Espera o hub conectar — ConnectionChanged re-sinaliza.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (hub.State ==
                    Microsoft.AspNetCore.SignalR.Client
                        .HubConnectionState.Connected)
                {
                    await foreach (var _ in hub.AttentionChangesAsync(ct))
                    {
                        Invalidate();
                    }
                }
            }
            catch (Exception) { /* reconnect/cancel — retenta */ }

            try { await Task.Delay(3000, ct); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task MaybeToastAsync(AttentionItemDto item)
    {
        try
        {
            // Opt-out: "0" desliga toasts de attention.
            var pref = await js.InvokeAsync<string?>(
                "localStorage.getItem",
                new object?[] { ToastOptOutKey });
            if (pref == "0")
            {
                return;
            }
        }
        catch (Exception) { }

        var title = item.Title ?? item.ConversationId[..8];
        toasts.Warn($"{title} precisa de você");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_watch is not null)
            {
                await _watch;
            }
        }
        catch (Exception) { }

        _cts.Dispose();
    }
}