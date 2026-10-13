using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>Org conversation quota — stored on the organization row (SPEC-08 RF-010).</summary>
public sealed class QuotaService(AppDbContext db, IAuditService audit) : IQuotaService
{
    /// <inheritdoc />
    public async Task<OrgQuota?> GetAsync(
        Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await db.Organizations.FindAsync([orgId], cancellationToken);
        return org is null ? null : new OrgQuota(org.QuotaDailyMax, org.QuotaConcurrentMax);
    }

    /// <inheritdoc />
    public async Task SetAsync(
        Guid orgId, int? dailyMax, int? concurrentMax,
        CancellationToken cancellationToken = default)
    {
        var org = await db.Organizations.FindAsync([orgId], cancellationToken)
            ?? throw new ArgumentException("org not found");
        org.QuotaDailyMax = dailyMax;
        org.QuotaConcurrentMax = concurrentMax;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            "org.quota_set", null, orgId, "organization", orgId.ToString(),
            metadata: new { dailyMax, concurrentMax },
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ClearAsync(
        Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await db.Organizations.FindAsync([orgId], cancellationToken);
        if (org is null)
            return false;
        org.QuotaDailyMax = null;
        org.QuotaConcurrentMax = null;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

/// <summary>Append-only audit trail (SPEC-08 RF-011).</summary>
public sealed class AuditService(AppDbContext db) : IAuditService
{
    /// <inheritdoc />
    public async Task RecordAsync(
        string action, string? actorUserId = null, Guid? orgId = null,
        string? targetType = null, string? targetId = null,
        string? ip = null, string? userAgent = null,
        object? metadata = null, CancellationToken cancellationToken = default)
    {
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            UserId = actorUserId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Ip = ip,
            UserAgent = userAgent,
            PayloadJson = metadata is null ? null : JsonSerializer.Serialize(metadata),
            Timestamp = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<AuditEntry> Items, int Total)> QueryAsync(
        string? userId, string? action, DateTimeOffset? from, DateTimeOffset? to,
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var q = db.AuditEvents.AsQueryable();
        if (!string.IsNullOrWhiteSpace(userId))
            q = q.Where(e => e.UserId == userId);
        if (!string.IsNullOrWhiteSpace(action))
            q = q.Where(e => e.Action == action);
        if (from is { } f)
            q = q.Where(e => e.TimestampTicks >= f.UtcTicks);
        if (to is { } t)
            q = q.Where(e => e.TimestampTicks <= t.UtcTicks);

        var total = await q.CountAsync(cancellationToken);
        var items = await q.OrderByDescending(e => e.TimestampTicks)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new AuditEntry(
                e.Id, e.UserId, e.OrgId, e.Action, e.TargetType, e.TargetId,
                e.Ip, e.UserAgent, e.PayloadJson, e.Timestamp))
            .ToListAsync(cancellationToken);
        return (items, total);
    }
}

/// <summary>Instance feature flags.</summary>
public sealed class FeatureFlagService(AppDbContext db) : IFeatureFlagService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<FeatureFlagInfo>> ListAsync(
        CancellationToken cancellationToken = default) =>
        await db.FeatureFlags.OrderBy(f => f.Name)
            .Select(f => new FeatureFlagInfo(f.Name, f.Enabled, f.Description))
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> GetAsync(
        string name, CancellationToken cancellationToken = default)
    {
        var flag = await db.FeatureFlags.FindAsync([name], cancellationToken);
        return flag?.Enabled ?? false;
    }

    /// <inheritdoc />
    public async Task SetAsync(
        string name, bool enabled, string? description,
        CancellationToken cancellationToken = default)
    {
        var flag = await db.FeatureFlags.FindAsync([name], cancellationToken);
        if (flag is null)
        {
            db.FeatureFlags.Add(new FeatureFlagRecord
            {
                Name = name, Enabled = enabled,
                Description = description, CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            flag.Enabled = enabled;
            if (description is not null)
                flag.Description = description;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string name, CancellationToken cancellationToken = default)
    {
        var flag = await db.FeatureFlags.FindAsync([name], cancellationToken);
        if (flag is null)
            return false;
        db.FeatureFlags.Remove(flag);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

/// <summary>
/// No-op analytics sink (SPEC-08 RF-011 parity — provider "none"). PostHog can
/// replace this behind the same contract.
/// </summary>
public sealed class NullAnalyticsService : IAnalyticsService
{
    /// <inheritdoc />
    public Task TrackAsync(
        string eventName, string? distinctId, object? properties,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
