using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>
/// SPEC-20 RF-004: upserts the org's <c>usage_records</c> row for the
/// current <c>yyyy-MM</c> period — one row per org/period aggregates all
/// metered tokens/cost.
/// </summary>
public sealed class UsageMeter(AppDbContext db) : IUsageMeter
{
    /// <inheritdoc />
    public async Task RecordTokensAsync(
        Guid orgId, long tokens, decimal? costUsd, CancellationToken ct)
    {
        if (tokens <= 0 && costUsd is null or <= 0)
        {
            return;
        }

        var period = DateTimeOffset.UtcNow.ToString("yyyy-MM");
        var row = await db.UsageRecords
            .FirstOrDefaultAsync(
                u => u.OrgId == orgId && u.Period == period, ct);
        if (row is null)
        {
            db.UsageRecords.Add(new UsageRecord
            {
                Id = Guid.NewGuid(), OrgId = orgId, Period = period,
                TokensUsed = tokens, CostUsd = costUsd,
                RecordedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            row.TokensUsed += tokens;
            if (costUsd is { } c)
            {
                row.CostUsd = (row.CostUsd ?? 0m) + c;
            }

            row.RecordedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }
}
