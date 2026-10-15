namespace OpenHandsNET.Client.Services;

/// <summary>
/// SPEC-26 RF-004: fila de toasts (info/warn/error/success) exibida pelo
/// <c>ToastHost</c>. Auto-dismiss com timeout por severidade.
/// </summary>
public sealed class ToastService
{
    public enum Level { Info, Success, Warn, Error }

    public sealed record Toast(Guid Id, Level Severity, string Message);

    private readonly List<Toast> _toasts = [];
    private readonly Lock _gate = new();

    /// <summary>Toasts ativos (snapshot para render).</summary>
    public IReadOnlyList<Toast> Toasts
    {
        get { lock (_gate) { return _toasts.ToArray(); } }
    }

    /// <summary>Dispara quando a lista muda.</summary>
    public event Action? Changed;

    public void Info(string message) => Push(Level.Info, message, 5);
    public void Success(string message) => Push(Level.Success, message, 4);
    public void Warn(string message) => Push(Level.Warn, message, 8);
    public void Error(string message) => Push(Level.Error, message, 12);

    private void Push(Level level, string message, int seconds)
    {
        var toast = new Toast(Guid.NewGuid(), level, message);
        lock (_gate)
        {
            _toasts.Add(toast);
            if (_toasts.Count > 5)
            {
                _toasts.RemoveAt(0);
            }
        }
        Changed?.Invoke();
        _ = AutoDismissAsync(toast.Id, seconds);
    }

    /// <summary>Remove um toast (dismiss manual).</summary>
    public void Dismiss(Guid id)
    {
        lock (_gate)
        {
            _toasts.RemoveAll(t => t.Id == id);
        }
        Changed?.Invoke();
    }

    private async Task AutoDismissAsync(Guid id, int seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        Dismiss(id);
    }
}
