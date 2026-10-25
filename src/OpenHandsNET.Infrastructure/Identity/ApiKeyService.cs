using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Identity;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>
/// sk-* API keys (SPEC-08 RF-003). Plaintext shown once; only SHA-256 of the
/// secret + indexed prefix are persisted. <c>last_used_at</c> is updated
/// fire-and-forget outside the hot path.
/// </summary>
public sealed class ApiKeyService(
    AppDbContext db,
    IServiceScopeFactory scopeFactory,
    ILogger<ApiKeyService> logger) : IApiKeyService
{
    internal const string KeyPrefix = "sk-";

    /// <summary>SPEC-57: prefixo de chaves de service user.</summary>
    internal const string ServiceKeyPrefix = "svc_";

    /// <inheritdoc />
    public async Task<ApiKeyCreateResult> CreateAsync(
        AuthUser user, string? name, DateTimeOffset? notBefore,
        DateTimeOffset? expiresAt, Guid? orgId,
        CancellationToken cancellationToken = default)
    {
        if (expiresAt is { } exp && exp <= DateTimeOffset.UtcNow)
            throw new ArgumentException("expires_at must be in the future");
        if (notBefore is { } nb && expiresAt is { } ex && nb >= ex)
            throw new ArgumentException("not_before must be before expires_at");

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var plaintext = KeyPrefix + secret;

        var record = new ApiKeyRecord
        {
            Id = Guid.NewGuid(),
            Prefix = plaintext[..12],
            UserId = user.UserId.ToString(),
            OrgId = orgId,
            KeyHash = Hash(plaintext),
            Name = name,
            NotBefore = notBefore,
            ExpiresAt = expiresAt,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ApiKeys.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("api key {KeyPrefix} created for user {UserId}", record.Prefix, user.UserId);
        return new ApiKeyCreateResult(ToInfo(record), plaintext);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApiKeyInfo>> ListAsync(
        AuthUser user, CancellationToken cancellationToken = default)
    {
        var rows = await db.ApiKeys
            .IgnoreQueryFilters()
            .Where(k => k.UserId == user.UserId.ToString())
            .ToListAsync(cancellationToken);
        // SQLite can't ORDER BY DateTimeOffset — sort in memory (per-user
        // key lists are small).
        return rows.OrderByDescending(k => k.CreatedAt)
            .Select(ToInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        AuthUser user, Guid keyId, CancellationToken cancellationToken = default)
    {
        var record = await db.ApiKeys.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                k => k.Id == keyId && k.UserId == user.UserId.ToString(),
                cancellationToken);
        if (record is null)
            return false;
        db.ApiKeys.Remove(record);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<ApiKeyResolution?> ValidateAsync(
        string presentedKey, CancellationToken cancellationToken = default)
    {
        var isService = presentedKey.StartsWith(
            ServiceKeyPrefix, StringComparison.Ordinal);
        if (!(isService
            || presentedKey.StartsWith(KeyPrefix, StringComparison.Ordinal))
            || presentedKey.Length < 14)
        {
            return null;
        }

        var hash = Hash(presentedKey);
        var now = DateTimeOffset.UtcNow;
        var record = await db.ApiKeys.IgnoreQueryFilters()
            .FirstOrDefaultAsync(k => k.KeyHash == hash, cancellationToken);
        if (record is null
            || (record.NotBefore is { } nb && nb > now)
            || (record.ExpiresAt is { } ex && ex <= now)
            || !Guid.TryParse(record.UserId, out var userId))
        {
            return null;
        }

        // SPEC-57: chave de service user — desabilitado revoga imediatamente.
        ServiceUserRecord? svc = null;
        if (record.ServiceUserId is { } suid)
        {
            svc = await db.ServiceUsers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.Id == suid, cancellationToken);
            if (svc is null || svc.DisabledAt is not null)
                return null;
        }

        var keyId = record.Id;
        _ = Task.Run(() => TouchLastUsedAsync(keyId, cancellationToken),
            CancellationToken.None);

        return svc is not null
            ? new ApiKeyResolution(
                record.Id, svc.Id, record.OrgId ?? svc.OrgId, record.Name,
                ActorKind.ServiceUser, svc.Id, svc.Name, svc.Role,
                ParseScopes(record.ScopesJson))
            : new ApiKeyResolution(
                record.Id, userId, record.OrgId, record.Name,
                Scopes: ParseScopes(record.ScopesJson));
    }

    private async Task TouchLastUsedAsync(Guid keyId, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var scoped = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await scoped.ApiKeys.IgnoreQueryFilters()
                .FirstOrDefaultAsync(k => k.Id == keyId, ct);
            if (row is not null)
            {
                row.LastUsedAt = DateTimeOffset.UtcNow;
                await scoped.SaveChangesAsync(ct);
            }
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "last_used_at update failed for key {KeyId}", keyId);
        }
    }

    /// <summary>Scopes serializados (null = chave sem restrição).</summary>
    internal static IReadOnlyList<string>? ParseScopes(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>SHA-256 hex of the presented key (indexed lookup).</summary>
    internal static string Hash(string plaintext) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));

    private static ApiKeyInfo ToInfo(ApiKeyRecord r) => new(
        r.Id, r.Prefix, r.Name,
        // svc_* service-user keys can carry non-GUID owners — don't 500.
        Guid.TryParse(r.UserId, out var uid) ? uid : Guid.Empty, r.OrgId,
        r.NotBefore, r.ExpiresAt, r.LastUsedAt, r.CreatedAt);
}