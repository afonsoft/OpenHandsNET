using Microsoft.AspNetCore.Components;
using OpenHandsNET.Client.Services;

namespace OpenHandsNET.Client.Pages.Admin;

/// <summary>
/// SPEC-21 RF-001: gate client-side das páginas <c>/admin/*</c> — sem
/// super-admin, redireciona para <c>/</c>. O enforcement real é server-side
/// (401/403 viram <see cref="AdminDeniedException"/> → <see cref="Error"/>).
/// </summary>
public abstract class AdminPageBase : ComponentBase
{
    /// <summary>Estado de admin cacheado do app shell.</summary>
    [Inject] protected AdminState Admin { get; set; } = default!;

    /// <summary>Cliente tipado dos endpoints admin.</summary>
    [Inject] protected AdminApiClient Api { get; set; } = default!;

    /// <summary>Navigation.</summary>
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    /// <summary>True até a checagem de admin terminar.</summary>
    protected bool Checking { get; private set; } = true;

    /// <summary>True quando o usuário passou no gate.</summary>
    protected bool Allowed { get; private set; }

    /// <summary>Erro amigável (403/500) mostrado no lugar do conteúdo.</summary>
    protected string? Error { get; set; }

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        if (!await Admin.EnsureLoadedAsync())
        {
            Nav.NavigateTo("/", replace: true);
            return;
        }

        Allowed = true;
        Checking = false;
        try
        {
            await LoadAsync();
        }
        catch (AdminDeniedException)
        {
            Error = "sem permissão para esta área (403)";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Carga inicial da página (só roda para super-admin).</summary>
    protected virtual Task LoadAsync() => Task.CompletedTask;
}
