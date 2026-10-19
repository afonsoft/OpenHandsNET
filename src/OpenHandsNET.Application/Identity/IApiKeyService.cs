namespace OpenHandsNET.Application.Identity;

/// <summary>API key lifecycle — sk-* keys hashed SHA-256 at rest (SPEC-08 RF-003).</summary>
public interface IApiKeyService
{
    /// <summary>Create a key for the user; plaintext returned once.</summary>
    Task<ApiKeyCreateResult> CreateAsync(
        AuthUser user, string? name, DateTimeOffset? notBefore,
        DateTimeOffset? expiresAt, Guid? orgId,
        CancellationToken cancellationToken = default);

    /// <summary>List keys visible in the effective-org scope.</summary>
    Task<IReadOnlyList<ApiKeyInfo>> ListAsync(
        AuthUser user, CancellationToken cancellationToken = default);

    /// <summary>Delete a key owned by the user (false = not found).</summary>
    Task<bool> DeleteAsync(
        AuthUser user, Guid keyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolve a presented <c>sk-*</c> secret to the owning user + bound org.
    /// Updates <c>last_used_at</c> asynchronously off the hot path.
    /// </summary>
    Task<ApiKeyResolution?> ValidateAsync(
        string presentedKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Validated API key — owning user (or service user) and optional org
/// binding. SPEC-57: <c>svc_*</c> resolve com ActorKind=ServiceUser,
/// ServiceUserId preenchido e <see cref="Scopes"/> da chave;
/// <c>sk-*</c> legadas mantêm ActorKind=User e Scopes=null (acesso total).
/// </summary>
public sealed record ApiKeyResolution(
    Guid KeyId, Guid UserId, Guid? BoundOrgId, string? Name,
    ActorKind ActorKind = ActorKind.User,
    Guid? ServiceUserId = null,
    string? ServiceUserName = null,
    string? Role = null,
    IReadOnlyList<string>? Scopes = null);