namespace OpenHandsNET.Application.Identity;

/// <summary>Quem atua na request (SPEC-57 RF-001).</summary>
public enum ActorKind
{
    /// <summary>Humano (cookie ou chave pessoal <c>sk-*</c>).</summary>
    User,

    /// <summary>Service user (chave <c>svc_*</c>) — identidade separada.</summary>
    ServiceUser,
}

/// <summary>Escopos declaráveis em chaves (SPEC-57 RF-002).</summary>
public static class ApiScopes
{
    public const string ConversationsRead = "conversations:read";
    public const string ConversationsWrite = "conversations:write";
    public const string SandboxesRead = "sandboxes:read";
    public const string SandboxesWrite = "sandboxes:write";
    public const string AutomationsRead = "automations:read";
    public const string AutomationsWrite = "automations:write";
    public const string AdminUsage = "admin:usage";
    public const string SessionsImpersonate = "sessions:impersonate";
    public const string SecretsWrite = "secrets:write";
    public const string ScmWrite = "scm:write";
    public const string TicketsWrite = "tickets:write";
    public const string ChatopsWrite = "chatops:write";
    public const string AdminServiceUsers = "admin:service_users";

    /// <summary>Todos os escopos conhecidos (validação de criação).</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        StringComparer.Ordinal)
    {
        ConversationsRead, ConversationsWrite,
        SandboxesRead, SandboxesWrite,
        AutomationsRead, AutomationsWrite,
        AdminUsage, SessionsImpersonate, SecretsWrite,
        ScmWrite, TicketsWrite, ChatopsWrite, AdminServiceUsers,
    };
}

/// <summary>Service user de uma org (SPEC-57 RF-001).</summary>
public sealed record ServiceUserInfo(
    Guid Id,
    Guid OrgId,
    string Name,
    string Role,
    Guid CreatedBy,
    DateTimeOffset? DisabledAt,
    DateTimeOffset CreatedAt);

/// <summary>Criação de service user.</summary>
public sealed record ServiceUserCreateRequest(
    string Name,
    string Role);

/// <summary>Atualização (rename/role/disable) — campos nulos mantêm.</summary>
public sealed record ServiceUserUpdateRequest(
    string? Name,
    string? Role,
    bool? Disabled);

/// <summary>Emissão de chave <c>svc_*</c> escopada.</summary>
public sealed record ServiceUserKeyCreateRequest(
    string? Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Service users: CRUD + emissão de chaves <c>svc_*</c> escopadas
/// (SPEC-57). Chaves resolvem via <see cref="IApiKeyService"/>.
/// </summary>
public interface IServiceUserService
{
    /// <summary>Lista service users da org (inclui desabilitados).</summary>
    Task<IReadOnlyList<ServiceUserInfo>> ListAsync(
        Guid orgId, CancellationToken ct = default);

    /// <summary>Cria um service user (role: owner|admin|member).</summary>
    Task<ServiceUserInfo> CreateAsync(
        Guid orgId, ServiceUserCreateRequest request, Guid createdBy,
        CancellationToken ct = default);

    /// <summary>Atualiza nome/role/disabled. Null = not found.</summary>
    Task<ServiceUserInfo?> UpdateAsync(
        Guid orgId, Guid id, ServiceUserUpdateRequest request,
        CancellationToken ct = default);

    /// <summary>Remove o service user (chaves dele passam a falhar).</summary>
    Task<bool> DeleteAsync(
        Guid orgId, Guid id, CancellationToken ct = default);

    /// <summary>Emite chave <c>svc_*</c> com interseção role∩scopes.</summary>
    Task<ApiKeyCreateResult> CreateKeyAsync(
        Guid orgId, Guid serviceUserId,
        ServiceUserKeyCreateRequest request,
        CancellationToken ct = default);

    /// <summary>Lista chaves de um service user.</summary>
    Task<IReadOnlyList<ApiKeyInfo>> ListKeysAsync(
        Guid orgId, Guid serviceUserId, CancellationToken ct = default);

    /// <summary>Revoga uma chave específica.</summary>
    Task<bool> DeleteKeyAsync(
        Guid orgId, Guid serviceUserId, Guid keyId,
        CancellationToken ct = default);
}
