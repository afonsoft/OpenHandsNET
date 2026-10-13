using System.Security.Cryptography;
using System.Text;
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
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(cancellationToken);
        return rows.Select(ToInfo).ToList();
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
        if (!presentedKey.StartsWith(KeyPrefix, StringComparison.Ordinal)
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

        var keyId = record.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var scoped = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var row = await scoped.ApiKeys.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(k => k.Id == keyId);
                if (row is not null)
                {
                    row.LastUsedAt = DateTimeOffset.UtcNow;
                    await scoped.SaveChangesAsync();
                }
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "last_used_at update failed for key {KeyId}", keyId);
            }
        }, CancellationToken.None);

        return new ApiKeyResolution(record.Id, userId, record.OrgId, record.Name);
    }

    /// <summary>SHA-256 hex of the presented key (indexed lookup).</summary>
    internal static string Hash(string plaintext) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));

    private static ApiKeyInfo ToInfo(ApiKeyRecord r) => new(
        r.Id, r.Prefix, r.Name, Guid.Parse(r.UserId), r.OrgId,
        r.NotBefore, r.ExpiresAt, r.LastUsedAt, r.CreatedAt);
}
