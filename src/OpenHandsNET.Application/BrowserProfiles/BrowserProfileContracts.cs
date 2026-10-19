namespace OpenHandsNET.Application.BrowserProfiles;

/// <summary>
/// SPEC-64 RF-001: metadados de um browser profile — o storage
/// state (credencial) nunca sai por aqui.
/// </summary>
public sealed record BrowserProfileInfo(
    Guid Id, Guid? OrgId, string OwnerId, string Domain,
    string Name, bool Shared, DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt, DateTimeOffset? ExpiresAt);

/// <summary>Falha específica de profile expirado/inválido (RF-003).</summary>
public sealed class BrowserProfileExpiredException(Guid profileId)
    : Exception($"browser profile {profileId} expired")
{
    public Guid ProfileId { get; } = profileId;
}

/// <summary>
/// SPEC-64: perfis de autenticação por domínio (storage state
/// Playwright criptografado em repouso — mesmo regime de secrets).
/// </summary>
public interface IBrowserProfileService
{
    /// <summary>Lista metas visíveis: do próprio usuário + shared da org.</summary>
    Task<IReadOnlyList<BrowserProfileInfo>> ListAsync(
        string ownerId, bool includeShared, CancellationToken ct);

    /// <summary>Meta de um profile (ou null se não visível).</summary>
    Task<BrowserProfileInfo?> GetAsync(
        Guid id, string ownerId, bool isAdmin, CancellationToken ct);

    /// <summary>
    /// Cria/atualiza o profile de <paramref name="domain"/> para o
    /// dono — <paramref name="storageStateJson"/> é criptografado
    /// antes de persistir. Retorna a meta.
    /// </summary>
    Task<BrowserProfileInfo> SaveAsync(
        string ownerId, string domain, string name,
        string storageStateJson, DateTimeOffset? expiresAt,
        CancellationToken ct);

    /// <summary>
    /// Resolve o storage state descriptografado se o caller pode
    /// anexar (dono ou shared da mesma org). Lança
    /// <see cref="BrowserProfileExpiredException"/> quando expirado.
    /// Atualiza <c>LastUsedAt</c>.
    /// </summary>
    Task<string> ResolveStateAsync(
        Guid id, string ownerId, bool isAdmin, CancellationToken ct);

    /// <summary>Remove o profile (dono ou admin).</summary>
    Task<bool> DeleteAsync(
        Guid id, string ownerId, bool isAdmin, CancellationToken ct);

    /// <summary>
    /// Normaliza para eTLD+1 simplificado (últimos 2 labels —
    /// limitação documentada: ccTLDs tipo .com.br viram o último
    /// par mesmo sendo TLD efetivo mais profundo).
    /// </summary>
    static string NormalizeDomain(string host)
    {
        var labels = host.Trim().TrimEnd('.')
            .Split('.', StringSplitOptions.RemoveEmptyEntries);
        return labels.Length <= 2
            ? string.Join('.', labels).ToLowerInvariant()
            : string.Join('.', labels[^2..]).ToLowerInvariant();
    }
}
