using Microsoft.JSInterop;

namespace OpenHandsNET.Client.Services;

/// <summary>
/// SPEC-120 (simplificação do OWUIC ChatNotificationsService): notificação
/// desktop via Notification API quando um item novo entra no inbox de
/// atenção (run concluída/falha/pergunta) e a aba está oculta. Opt-in
/// local (<c>localStorage["ohn.notify.browser"]</c>) — a permissão só é
/// pedida no gesto do toggle em Settings, nunca no boot.
/// </summary>
public sealed class ChatNotificationsService : IDisposable
{
    /// <summary>Chave localStorage da preferência (default desligado).</summary>
    public const string PrefKey = "ohn.notify.browser";

    private readonly AttentionInboxState _inbox;
    private readonly IJSRuntime _js;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _started;

    public ChatNotificationsService(AttentionInboxState inbox, IJSRuntime js)
    {
        _inbox = inbox;
        _js = js;
    }

    /// <summary>Liga o observador — idempotente, chamar no boot do app.</summary>
    public Task EnsureStartedAsync()
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        foreach (var item in _inbox.Items)
        {
            _seen.Add(item.ConversationId + item.Kind);
        }

        _inbox.Changed += OnInboxChanged;
        return Task.CompletedTask;
    }

    /// <summary>Lê a preferência opt-in (default off).</summary>
    public async Task<bool> GetPrefAsync()
    {
        try
        {
            return await _js.InvokeAsync<string?>(
                "localStorage.getItem", PrefKey) == "1";
        }
        catch (Exception) { return false; }
    }

    /// <summary>Persiste a preferência; ao ligar, pede a permissão do
    /// navegador (gesto do usuário).</summary>
    public async Task<bool> SetPrefAsync(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var perm = await _js.InvokeAsync<string>("ohnNotify.request");
                if (perm != "granted")
                {
                    return false;
                }
            }

            await _js.InvokeVoidAsync(
                "localStorage.setItem", PrefKey, enabled ? "1" : "0");
            return true;
        }
        catch (Exception) { return false; }
    }

    private async void OnInboxChanged()
    {
        try
        {
            if (!await GetPrefAsync())
            {
                return;
            }

            var hidden = await _js.InvokeAsync<bool>("ohnNotify.hidden");
            if (!hidden)
            {
                return;
            }

            foreach (var item in _inbox.Items)
            {
                var key = item.ConversationId + item.Kind;
                if (!_seen.Add(key))
                {
                    continue;
                }

                await _js.InvokeVoidAsync("ohnNotify.show",
                    $"OpenHandsNET — {item.Title ?? item.ConversationId[..8]}",
                    new
                    {
                        body = item.Excerpt ?? item.Kind,
                        tag = $"ohn-{key}",
                        url = $"/conversations/{item.ConversationId}",
                    });
            }
        }
        catch (Exception) { /* best-effort — JS indisponível */ }
    }

    public void Dispose() => _inbox.Changed -= OnInboxChanged;
}
