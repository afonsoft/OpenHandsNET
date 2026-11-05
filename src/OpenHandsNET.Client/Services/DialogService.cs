namespace OpenHandsNET.Client.Services;

/// <summary>Fila de diálogos modais in-app (confirm/prompt) — substitui
/// <c>window.confirm</c>/<c>window.prompt</c> nativos por um dialog
/// acessível renderizado pelo <c>DialogHost</c> no layout.</summary>
public sealed class DialogService
{
    /// <summary>Pedido de diálogo atualmente exibido (null = fechado).</summary>
    public DialogRequest? Current { get; private set; }

    /// <summary>Disparado quando o pedido atual muda (abrir/fechar).</summary>
    public event Action? Changed;

    /// <summary>Abre um diálogo de confirmação; resolve <c>true</c> se o usuário
    /// confirmar, <c>false</c> ao cancelar ou pressionar Escape.</summary>
    public async Task<bool> ConfirmAsync(string message, bool danger = false)
    {
        var result = await ShowAsync(new DialogRequest(DialogKind.Confirm, message, danger, null));
        return result is true;
    }

    /// <summary>Abre um diálogo com campo de texto; resolve o valor digitado
    /// ou <c>null</c> ao cancelar/pressionar Escape.</summary>
    public async Task<string?> PromptAsync(string message, string? defaultValue = null)
    {
        var result = await ShowAsync(new DialogRequest(DialogKind.Prompt, message, false, defaultValue));
        return result as string;
    }

    /// <summary>SPEC-119 RF-005: mostra um link copiável (share) — input
    /// readonly + botão copiar; resolve ao fechar.</summary>
    public Task<object?> ShowLinkAsync(string url) =>
        ShowAsync(new DialogRequest(DialogKind.Link, url, false, url));

    /// <summary>SPEC-119 RF-006: lightbox de imagem — overlay com zoom,
    /// ESC/backdrop fecham.</summary>
    public Task<object?> ShowImageAsync(string url, string? alt = null) =>
        ShowAsync(new DialogRequest(DialogKind.Lightbox, url, false, alt));

    /// <summary>Fecha o diálogo atual resolvendo o pendente com o resultado.</summary>
    public void Resolve(object? result)
    {
        if (Current is { } current)
        {
            Current = null;
            current.Completion.TrySetResult(result);
            Changed?.Invoke();
        }
    }

    private Task<object?> ShowAsync(DialogRequest request)
    {
        Current = request;
        Changed?.Invoke();
        return request.Completion.Task;
    }
}

/// <summary>Tipo do diálogo exibido.</summary>
public enum DialogKind
{
    /// <summary>Confirmação sim/não.</summary>
    Confirm,

    /// <summary>Entrada de texto livre.</summary>
    Prompt,

    /// <summary>Link copiável (share) — input readonly + copiar.</summary>
    Link,

    /// <summary>Overlay de imagem (lightbox) — ESC/backdrop fecham.</summary>
    Lightbox,
}

/// <summary>Pedido de diálogo pendente — dados para render e o TCS de resposta.</summary>
public sealed record DialogRequest(DialogKind Kind, string Message, bool Danger, string? DefaultValue)
{
    /// <summary>Resolução do diálogo: <c>true</c> (confirm), <c>string</c> (prompt)
    /// ou <c>null</c>/<c>false</c> (cancelado).</summary>
    public TaskCompletionSource<object?> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}