using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Identity;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>
/// Service users (SPEC-57): identidades não-humanas por org com chaves
/// <c>svc_*</c> escopadas. Escopos efetivos = interseção role∩scopes da
/// chave — validada na emissão (role não-elevável).
/// </summary>
public sealed class ServiceUserService(AppDbContext db) : IServiceUserService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ServiceUserInfo>> ListAsync(
        Guid orgId, CancellationToken ct = default) =>
        (await db.ServiceUsers.IgnoreQueryFilters()
            .Where(s => s.OrgId == orgId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct))
        .Select(ToInfo).ToList();

    /// <inheritdoc />
    public async Task<ServiceUserInfo> CreateAsync(
        Guid orgId, ServiceUserCreateRequest request, Guid createdBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("name required");
        if (!RoleName.IsOrgRole(request.Role))
            throw new ArgumentException("role must be owner|admin|member");

        var record = new ServiceUserRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Name = request.Name.Trim(),
            Role = request.Role,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ServiceUsers.Add(record);
        await db.SaveChangesAsync(ct);
        return ToInfo(record);
    }

    /// <inheritdoc />
    public async Task<ServiceUserInfo?> UpdateAsync(
        Guid orgId, Guid id, ServiceUserUpdateRequest request,
        CancellationToken ct = default)
    {
        var record = await db.ServiceUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == id && s.OrgId == orgId, ct);
        if (record is null)
            return null;

        if (request.Name is { } name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("name must be non-empty");
            record.Name = name.Trim();
        }
        if (request.Role is { } role)
        {
            if (!RoleName.IsOrgRole(role))
                throw new ArgumentException("role must be owner|admin|member");
            record.Role = role;
        }
        if (request.Disabled is { } disabled)
            record.DisabledAt = disabled
                ? record.DisabledAt ?? DateTimeOffset.UtcNow : null;

        await db.SaveChangesAsync(ct);
        return ToInfo(record);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        Guid orgId, Guid id, CancellationToken ct = default)
    {
        var record = await db.ServiceUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == id && s.OrgId == orgId, ct);
        if (record is null)
            return false;
        db.ServiceUsers.Remove(record);
        // Revoga as chaves junto — defesa em profundidade sobre o
        // DisabledAt check do ValidateAsync.
        var keys = await db.ApiKeys.IgnoreQueryFilters()
            .Where(k => k.ServiceUserId == id).ToListAsync(ct);
        db.ApiKeys.RemoveRange(keys);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<ApiKeyCreateResult> CreateKeyAsync(
        Guid orgId, Guid serviceUserId,
        ServiceUserKeyCreateRequest request,
        CancellationToken ct = default)
    {
        var svc = await db.ServiceUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                s => s.Id == serviceUserId && s.OrgId == orgId, ct)
            ?? throw new ArgumentException("service user not found");
        if (svc.DisabledAt is not null)
            throw new ArgumentException("service user is disabled");
        if (request.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow)
            throw new ArgumentException("expires_at must be in the future");

        var scopes = request.Scopes?.Distinct(StringComparer.Ordinal).ToList()
            ?? [];
        var unknown = scopes.Where(s => !ApiScopes.All.Contains(s)).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException(
                $"unknown scopes: {string.Join(", ", unknown)}");

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var plaintext = ApiKeyService.ServiceKeyPrefix + secret;
        var record = new ApiKeyRecord
        {
            Id = Guid.NewGuid(),
            Prefix = plaintext[..12],
            // UserId é required no record; para svc_* guarda o id do
            // service user — ValidateAsync resolve via ServiceUserId.
            UserId = svc.Id.ToString(),
            OrgId = orgId,
            KeyHash = ApiKeyService.Hash(plaintext),
            Name = request.Name,
            ScopesJson = JsonSerializer.Serialize(scopes),
            ServiceUserId = svc.Id,
            ExpiresAt = request.ExpiresAt,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ApiKeys.Add(record);
        await db.SaveChangesAsync(ct);
        return new ApiKeyCreateResult(ToKeyInfo(record), plaintext);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApiKeyInfo>> ListKeysAsync(
        Guid orgId, Guid serviceUserId, CancellationToken ct = default) =>
        (await db.ApiKeys.IgnoreQueryFilters()
            .Where(k => k.ServiceUserId == serviceUserId
                && k.OrgId == orgId)
            .OrderByDescending(k => k.CreatedAt).ToListAsync(ct))
        .Select(ToKeyInfo).ToList();

    /// <inheritdoc />
    public async Task<bool> DeleteKeyAsync(
        Guid orgId, Guid serviceUserId, Guid keyId,
        CancellationToken ct = default)
    {
        var key = await db.ApiKeys.IgnoreQueryFilters()
            .FirstOrDefaultAsync(k => k.Id == keyId
                && k.ServiceUserId == serviceUserId
                && k.OrgId == orgId, ct);
        if (key is null)
            return false;
        db.ApiKeys.Remove(key);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static ServiceUserInfo ToInfo(ServiceUserRecord r) => new(
        r.Id, r.OrgId!.Value, r.Name, r.Role, r.CreatedBy,
        r.DisabledAt, r.CreatedAt);

    private static ApiKeyInfo ToKeyInfo(ApiKeyRecord r) => new(
        r.Id, r.Prefix, r.Name, Guid.Parse(r.UserId), r.OrgId,
        r.NotBefore, r.ExpiresAt, r.LastUsedAt, r.CreatedAt);
}