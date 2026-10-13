using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>
/// <see cref="IUsageService"/> over EF: <c>usage_records</c> supplies
/// tokens/cost for the calendar period; sandbox seconds come from live
/// sandbox rows of org conversations (running → now, terminal → expires);
/// runs = conversations created in the period.
/// </summary>
public sealed class UsageService(AppDbContext db) : IUsageService
{
    /// <inheritdoc />
    public async Task<OrgUsage> GetOrgUsageAsync(Guid orgId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var period = now.ToString("yyyy-MM");
        var periodStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        var usage = await db.UsageRecords.AsNoTracking()
            .Where(u => u.OrgId == orgId && u.Period == period)
            .GroupBy(u => 1)
            .Select(g => new
            {
                Tokens = g.Sum(u => u.TokensUsed),
                Cost = g.Sum(u => u.CostUsd),
            })
            .FirstOrDefaultAsync(ct);

        var convIds = db.Conversations.AsNoTracking()
            .Where(c => c.OrgId == orgId)
            .Select(c => c.Id);

        var nowTicks = now.UtcTicks;
        var sandboxes = await db.Sandboxes.AsNoTracking()
            .Where(s => s.ConversationId != null && convIds.Contains(s.ConversationId))
            .Select(s => new
            {
                s.Status, s.CreatedAt,
                ExpiresTicks = s.ExpiresAtTicks ?? nowTicks,
            })
            .ToListAsync(ct);

        var seconds = sandboxes.Sum(s =>
            Math.Max(0,
                (s.Status == nameof(SandboxStatus.Running) ||
                 s.Status == nameof(SandboxStatus.Starting)
                    ? nowTicks
                    : Math.Min(s.ExpiresTicks, nowTicks))
                - s.CreatedAt.UtcTicks) / (double)TimeSpan.TicksPerSecond);
        var active = sandboxes.Count(s =>
            s.Status is nameof(SandboxStatus.Running) or nameof(SandboxStatus.Starting));
        var runs = await db.Conversations.AsNoTracking()
            .CountAsync(c => c.OrgId == orgId && c.CreatedAtTicks >= periodStart.UtcTicks, ct);

        return new OrgUsage(
            orgId, period, usage?.Tokens ?? 0, usage?.Cost,
            Math.Round(seconds), runs, active);
    }
}
