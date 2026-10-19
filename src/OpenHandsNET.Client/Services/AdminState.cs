namespace OpenHandsNET.Client.Services;

/// <summary>
/// SPEC-21 RF-001: estado de admin do usuário atual — resolvido uma vez
/// (<c>/admin/super-admins/me</c>) e cacheado para o gate de nav/rotas.
/// Enforcement real continua server-side; isto é só UX.
/// </summary>
public sealed class AdminState(AdminApiClient api)
{
    private bool _loaded;

    /// <summary>True quando o usuário autenticado é super-admin.</summary>
    public bool IsSuperAdmin { get; private set; }

    /// <summary>Resolve o flag uma vez; chamadas seguintes retornam o cache.</summary>
    public async Task<bool> EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded)
        {
            return IsSuperAdmin;
        }

        IsSuperAdmin = await api.IsSuperAdminAsync(ct);
        _loaded = true;
        return IsSuperAdmin;
    }

    /// <summary>Revalida (pós grant/revoke na mesma sessão).</summary>
    public void Invalidate() => _loaded = false;
}