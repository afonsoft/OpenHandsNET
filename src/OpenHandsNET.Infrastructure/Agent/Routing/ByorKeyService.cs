using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Agent.Routing;

/// <summary>
/// SPEC-50 RF-002/003: BYOR key lifecycle — one active virtual key per
/// user; plaintext persists only as DataProtection ciphertext and leaves
/// the API once at refresh. <see cref="PermittedModelsAsync"/> caches the
/// proxy <c>/models</c> list for 60s (the <c>byor</c> allowlist).
/// </summary>
public sealed class ByorKeyService(
    AppDbContext db,
    ILiteLlmAdminClient admin,
    IOptions<LlmProxyOptions> options,
    IDataProtectionProvider protection,
    IDistributedCache? cache = null) : IByorKeyService
{
    private const string Purpose = "openhandsnet.byor.v1";
    private const string PermittedCacheKey = "byor:permitted-models";

    /// <inheritdoc />
    public bool Enabled => options.Value.Enabled;

    /// <inheritdoc />
    public async Task<ByorKeyInfo?> GetActiveAsync(
        string userId, CancellationToken ct)
    {
        var row = await db.ByorKeys.AsNoTracking()
            .Where(k => k.UserId == userId && k.RevokedAt == null)
            .OrderByDescending(k => k.CreatedAtTicks)
            .FirstOrDefaultAsync(ct);
        return row is null ? null : ToInfo(row);
    }

    /// <inheritdoc />
    public async Task<(string Key, ByorKeyInfo Info)> RefreshAsync(
        string userId, string? orgId, decimal? budgetUsd, CancellationToken ct)
    {
        EnsureEnabled();
        var budget = budgetUsd ?? options.Value.DefaultBudgetUsd;

        // Revoga a anterior primeiro (proxy + local) — rotação atômica lógica.
        var old = await db.ByorKeys
            .Where(k => k.UserId == userId && k.RevokedAt == null)
            .OrderByDescending(k => k.CreatedAtTicks)
            .FirstOrDefaultAsync(ct);
        if (old is not null)
        {
            await TryDeleteRemoteAsync(old.KeyId, ct);
            old.RevokedAt = DateTimeOffset.UtcNow;
        }

        var models = await PermittedModelsAsync(ct);
        var created = await admin.CreateVirtualKeyAsync(
            userId, orgId, budget, models, ct);

        var nonce = Guid.NewGuid().ToString("N");
        var record = new ByorKeyRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrgId = Guid.TryParse(orgId, out var og) ? og : null,
            KeyId = created.KeyId,
            KeyPrefix = created.Key[..Math.Min(16, created.Key.Length)],
            KeyCiphertext = protection.CreateProtector(Purpose, nonce)
                .Protect(created.Key),
            Nonce = nonce,
            BudgetUsd = budget,
            ModelsJson = models.Count > 0 ? JsonSerializer.Serialize(models) : null,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedAtTicks = DateTimeOffset.UtcNow.UtcTicks,
        };
        db.ByorKeys.Add(record);
        await db.SaveChangesAsync(ct);
        return (created.Key, ToInfo(record));
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(string userId, CancellationToken ct)
    {
        EnsureEnabled();
        var row = await db.ByorKeys
            .Where(k => k.UserId == userId && k.RevokedAt == null)
            .OrderByDescending(k => k.CreatedAtTicks)
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return false;
        }

        await TryDeleteRemoteAsync(row.KeyId, ct);
        row.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<ByorCallContext?> ResolveCallContextAsync(
        string userId, CancellationToken ct)
    {
        if (!Enabled)
        {
            return null;
        }

        var row = await db.ByorKeys.AsNoTracking()
            .Where(k => k.UserId == userId && k.RevokedAt == null)
            .OrderByDescending(k => k.CreatedAtTicks)
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }

        var key = protection.CreateProtector(Purpose, row.Nonce)
            .Unprotect(row.KeyCiphertext);
        return new ByorCallContext(
            options.Value.BaseUrl!.TrimEnd('/'), key, row.KeyId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> PermittedModelsAsync(
        CancellationToken ct)
    {
        EnsureEnabled();
        if (cache is not null)
        {
            var cached = await cache.GetStringAsync(PermittedCacheKey, ct);
            if (cached is not null)
            {
                return JsonSerializer.Deserialize<List<string>>(cached) ?? [];
            }
        }

        var models = await admin.ListModelsAsync(ct);
        if (cache is not null)
        {
            await cache.SetStringAsync(PermittedCacheKey,
                JsonSerializer.Serialize(models),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60),
                }, ct);
        }

        return models;
    }

    private async Task TryDeleteRemoteAsync(string keyId, CancellationToken ct)
    {
        try
        {
            await admin.DeleteKeyAsync(keyId, ct);
        }
        catch (LiteLlmUnavailableException)
        {
            // Best-effort: a revogação local procede mesmo se o proxy falhou —
            // a key marcada deixa de resolver call-context.
        }
    }

    private void EnsureEnabled()
    {
        if (!Enabled)
        {
            throw new LiteLlmUnavailableException(
                "LiteLLM proxy not configured (LlmProxy)");
        }
    }

    private static ByorKeyInfo ToInfo(ByorKeyRecord r) => new(
        r.Id, r.KeyId, r.KeyPrefix, r.BudgetUsd,
        r.ModelsJson is null ? []
            : JsonSerializer.Deserialize<List<string>>(r.ModelsJson) ?? [],
        r.CreatedAt);
}
