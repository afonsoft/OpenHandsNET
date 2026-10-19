namespace OpenHandsNET.Application.Identity;

/// <summary>Chave SCIM emitida (SPEC-58 RF-001) — plaintext só aqui.</summary>
public sealed record ScimTokenIssue(Guid Id, string Prefix, string Plaintext);

/// <summary>Tokens SCIM por org: mint + validação (hash-only at rest).</summary>
public interface IScimTokenService
{
    Task<ScimTokenIssue> MintAsync(
        Guid orgId, string? name, CancellationToken ct);

    /// <summary>Resolve o token para a org dona (null = inválido).</summary>
    Task<Guid?> ValidateAsync(string presented, CancellationToken ct);

    Task<IReadOnlyList<(Guid Id, string Prefix, string? Name, DateTimeOffset CreatedAt)>>
        ListAsync(Guid orgId, CancellationToken ct);

    Task<bool> RevokeAsync(Guid orgId, Guid tokenId, CancellationToken ct);
}

/// <summary>Mapping grupo IdP → org/role/tier.</summary>
public sealed record IdpMappingInfo(
    Guid Id, Guid OrgId, string IdpGroup, string Role, Guid? UsageTierId);

/// <summary>Tier de quota por org.</summary>
public sealed record UsageTierInfo(
    Guid Id, Guid OrgId, string Name, string? LimitsJson);

/// <summary>Override manual de quota por usuário.</summary>
public sealed record UserOverrideInfo(
    Guid Id, Guid OrgId, Guid UserId, string? LimitsJson,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// SPEC-58 RF-002: no login/authenticate, aplica grupos do IdP às
/// memberships (idempotente). Memberships com <c>Source=idp</c> cujo
/// mapping sumiu são revogadas; memberships manuais nunca são tocadas.
/// </summary>
public interface IIdpSyncService
{
    /// <summary>Aplica claims <c>groups</c> do token — retorna os mapping ids
    /// que casaram (para audit <c>idp.group_matched</c>).</summary>
    Task<IReadOnlyList<IdpMappingInfo>> ApplyOnLoginAsync(
        Guid userId, IReadOnlyList<string> idpGroups, CancellationToken ct);
}

/// <summary>CRUD admin de mappings, tiers e overrides (SPEC-58 RF-003/004).</summary>
public interface IIdentityAdminService
{
    Task<IReadOnlyList<IdpMappingInfo>> ListMappingsAsync(
        Guid orgId, CancellationToken ct);

    Task<IdpMappingInfo> CreateMappingAsync(
        Guid orgId, string idpGroup, string role, Guid? usageTierId,
        CancellationToken ct);

    Task<bool> DeleteMappingAsync(Guid orgId, Guid id, CancellationToken ct);

    Task<IReadOnlyList<UsageTierInfo>> ListTiersAsync(
        Guid orgId, CancellationToken ct);

    Task<UsageTierInfo> CreateTierAsync(
        Guid orgId, string name, string? limitsJson, CancellationToken ct);

    Task<UsageTierInfo?> UpdateTierAsync(
        Guid orgId, Guid id, string? name, string? limitsJson,
        CancellationToken ct);

    Task<bool> DeleteTierAsync(Guid orgId, Guid id, CancellationToken ct);

    Task<UserOverrideInfo> SetUserOverrideAsync(
        Guid orgId, Guid userId, Guid actorId, string? limitsJson,
        DateTimeOffset? expiresAt, CancellationToken ct);

    Task<UserOverrideInfo?> GetUserOverrideAsync(
        Guid orgId, Guid userId, CancellationToken ct);

    Task<bool> ClearUserOverrideAsync(
        Guid orgId, Guid userId, CancellationToken ct);
}