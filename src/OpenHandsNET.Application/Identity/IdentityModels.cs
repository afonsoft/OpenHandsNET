namespace OpenHandsNET.Application.Identity;

// Modelos de transporte identidade/orgs (SPEC-08). snake_case é aplicado
// pelo serializer global da API — os tipos usam nomes CLR aqui.

/// <summary>Authenticated principal resolved for the request.</summary>
public sealed record AuthUser(
    Guid UserId,
    string Email,
    string? DisplayName,
    bool IsSuperAdmin,
    string? CurrentOrgId,
    AuthType AuthType,
    Guid? BoundOrgId = null,
    string? IdpType = null,
    ActorKind ActorKind = ActorKind.User,
    string? Role = null,
    IReadOnlyList<string>? Scopes = null);

/// <summary>How the request authenticated (SPEC-08 RF-002).</summary>
public enum AuthType
{
    Cookie,
    Bearer,
}

/// <summary>Effective-org resolution result for the request.</summary>
public sealed record EffectiveOrg(Guid OrgId, string Source);

/// <summary>API key metadata (hash never leaves the store).</summary>
public sealed record ApiKeyInfo(
    Guid Id,
    string Prefix,
    string? Name,
    Guid UserId,
    Guid? OrgId,
    DateTimeOffset? NotBefore,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset CreatedAt);

/// <summary>Create result — plaintext key is returned exactly once.</summary>
public sealed record ApiKeyCreateResult(ApiKeyInfo Key, string Plaintext);

/// <summary>Organization projection.</summary>
public sealed record OrgInfo(
    Guid Id,
    string Slug,
    string Name,
    Guid? PlanId,
    bool Suspended,
    string? Role,
    DateTimeOffset CreatedAt);

/// <summary>Org member projection.</summary>
public sealed record MemberInfo(
    Guid UserId,
    string Email,
    string? DisplayName,
    string Role,
    DateTimeOffset CreatedAt);

/// <summary>Invitation projection.</summary>
public sealed record InvitationInfo(
    Guid Id,
    string Token,
    Guid OrgId,
    string Email,
    string Role,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt);

/// <summary>Provisioned-user result (broker + local records).</summary>
public sealed record ProvisionedUser(
    string Email,
    string? Password,
    string ApiKey);

/// <summary>Device-flow authorization session.</summary>
public sealed record DeviceAuthorization(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    int ExpiresIn,
    int Interval);

/// <summary>Org conversation quota.</summary>
public sealed record OrgQuota(int? DailyMax, int? ConcurrentMax);

/// <summary>Super-admin user projection.</summary>
public sealed record SuperAdminInfo(Guid UserId, string Email, string? DisplayName);

/// <summary>Paginated audit query result.</summary>
public sealed record AuditEntry(
    Guid Id,
    string? ActorUserId,
    Guid? OrgId,
    string Action,
    string? TargetType,
    string? TargetId,
    string? Ip,
    string? UserAgent,
    string? PayloadJson,
    DateTimeOffset Timestamp);

/// <summary>Feature flag row.</summary>
public sealed record FeatureFlagInfo(string Name, bool Enabled, string? Description);
