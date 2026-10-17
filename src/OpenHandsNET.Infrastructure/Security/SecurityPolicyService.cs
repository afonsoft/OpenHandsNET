using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Security;

/// <summary>
/// SPEC-48 RF-001/003: org security policy — effective resolution
/// (preset + overrides), distributed cache (TTL 60s) with invalidation
/// on write. Missing rows resolve to <c>balanced</c> (fail-open).
/// </summary>
public sealed class SecurityPolicyService(
    AppDbContext db, IDistributedCache? cache = null) : ISecurityPolicyService
{
    private static readonly DistributedCacheEntryOptions CacheEntry = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60),
    };

    private static string Key(Guid orgId) => $"ohn:sec-policy:{orgId:N}";

    /// <inheritdoc />
    public async Task<SecurityPolicy> GetAsync(Guid? orgId, CancellationToken ct)
    {
        if (orgId is null)
        {
            return SecurityPolicy.Balanced;
        }

        var key = Key(orgId.Value);
        if (cache is not null)
        {
            var hit = await cache.GetStringAsync(key, ct);
            if (hit is not null)
            {
                var cached = JsonSerializer.Deserialize<SecurityPolicy>(hit);
                if (cached is not null)
                {
                    return cached;
                }
            }
        }

        var row = await db.SecurityPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.OrgId == orgId.Value, ct);
        var policy = row is null
            ? SecurityPolicy.Balanced
            : SecurityPolicyPresets.Resolve(row.Preset, new SecurityPolicyUpdate(
                null, row.AnalyzerOverride, row.AskMcpTools,
                row.MinConfirmRisk, row.ResumeStrict));

        if (cache is not null)
        {
            await cache.SetStringAsync(
                key, JsonSerializer.Serialize(policy), CacheEntry, ct);
        }

        return policy;
    }

    /// <inheritdoc />
    public async Task<SecurityPolicy> SetAsync(
        Guid orgId, SecurityPolicyUpdate? update, Guid? actorUserId,
        CancellationToken ct)
    {
        var preset = (update?.Preset ?? "balanced").ToLowerInvariant();
        if (!SecurityPolicyPresets.Names.Contains(preset))
        {
            throw new ArgumentException(
                $"invalid preset '{update?.Preset}'", nameof(update));
        }
        if (update?.AnalyzerOverride is { Length: > 0 } ao
            && !SecurityPolicyPresets.Analyzers.Contains(ao))
        {
            throw new ArgumentException(
                $"invalid analyzer_override '{ao}'", nameof(update));
        }
        if (update?.MinConfirmRisk is { Length: > 0 } mr
            && !SecurityPolicyPresets.Risks.Contains(mr))
        {
            throw new ArgumentException(
                $"invalid min_confirm_risk '{mr}'", nameof(update));
        }

        var row = await db.SecurityPolicies
            .FirstOrDefaultAsync(r => r.OrgId == orgId, ct);
        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            row = new SecurityPolicyRecord
            {
                Id = Guid.NewGuid(), OrgId = orgId,
                Preset = preset, CreatedAt = now, UpdatedAt = now,
            };
            db.SecurityPolicies.Add(row);
        }
        else
        {
            row.Preset = preset;
            row.UpdatedAt = now;
        }

        row.AnalyzerOverride = update?.AnalyzerOverride?.ToLowerInvariant();
        row.AskMcpTools = update?.AskMcpTools;
        row.MinConfirmRisk = update?.MinConfirmRisk?.ToLowerInvariant();
        row.ResumeStrict = update?.ResumeStrict;
        row.UpdatedByUserId = actorUserId;

        await db.SaveChangesAsync(ct);
        if (cache is not null)
        {
            await cache.RemoveAsync(Key(orgId), ct);
        }

        return SecurityPolicyPresets.Resolve(row.Preset,
            new SecurityPolicyUpdate(null, row.AnalyzerOverride,
                row.AskMcpTools, row.MinConfirmRisk, row.ResumeStrict));
    }
}
