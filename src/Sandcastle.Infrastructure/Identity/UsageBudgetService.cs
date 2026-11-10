using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Identity;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Identity;

/// <summary>
/// <see cref="IUsageBudgetService"/> over EF (SPEC-147 RF-004). Consumo
/// no escopo org vem de <c>usage_records</c> (mesma fonte do
/// enforcement); no escopo caller e no breakdown por modelo soma os
/// payloads <c>TokenEvent</c> do período corrente.
/// </summary>
public sealed class UsageBudgetService(
    AppDbContext db, IPlanEnforcementService plans) : IUsageBudgetService
{
    /// <inheritdoc />
    public async Task<UsageBudget> GetBudgetAsync(
        Guid userId, Guid? orgId, bool orgScope, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var periodStart = new DateTimeOffset(
            now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = periodStart.AddMonths(1);
        var period = now.ToString("yyyy-MM");

        var limit = (await plans.GetEffectiveLimitsAsync(
            orgId, userId, ct)).Limits.MaxTokensPerMonth;

        long consumed;
        List<ModelUsage> byModel;
        if (orgScope && orgId is { } oid)
        {
            consumed = await db.UsageRecords.AsNoTracking()
                .Where(u => u.OrgId == oid && u.Period == period)
                .SumAsync(u => (long?)u.TokensUsed, ct) ?? 0;
            byModel = await ByModelAsync(
                c => c.OrgId == oid, periodStart, periodEnd, ct);
        }
        else
        {
            var uid = userId.ToString();
            byModel = await ByModelAsync(
                c => c.CreatedByUserId == uid, periodStart, periodEnd, ct);
            consumed = byModel.Sum(m => m.Tokens);
        }

        return new UsageBudget(
            limit, consumed,
            limit is { } l ? Math.Max(0, l - consumed) : null,
            periodStart, periodEnd, byModel);
    }

    /// <summary>
    /// Agrupa <c>TokenEvent</c>s por modelo sobre as conversas que
    /// casam com <paramref name="convFilter"/> no período
    /// [start, end). Payloads são JSON snake_case do V1 event model.
    /// </summary>
    private async Task<List<ModelUsage>> ByModelAsync(
        System.Linq.Expressions.Expression<Func<
            Conversations.ConversationRecord, bool>> convFilter,
        DateTimeOffset start, DateTimeOffset end,
        CancellationToken ct)
    {
        var convIds = db.Conversations.AsNoTracking()
            .Where(convFilter).Select(c => c.Id);
        var payloads = await db.Events.AsNoTracking()
            .Where(e => e.Kind == "TokenEvent"
                && e.TimestampTicks >= start.UtcTicks
                && e.TimestampTicks < end.UtcTicks
                && convIds.Contains(e.ConversationId))
            .Select(e => e.Payload)
            .ToListAsync(ct);

        var acc = new Dictionary<string, (long Tokens, decimal Cost)>(
            StringComparer.Ordinal);
        foreach (var payload in payloads)
        {
            string? model = null;
            long tokens = 0;
            decimal cost = 0m;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.TryGetProperty("model", out var m))
                {
                    model = m.GetString();
                }
                if (root.TryGetProperty("prompt_tokens", out var p)
                    && p.ValueKind == JsonValueKind.Number)
                {
                    tokens += p.GetInt64();
                }
                if (root.TryGetProperty("completion_tokens", out var c)
                    && c.ValueKind == JsonValueKind.Number)
                {
                    tokens += c.GetInt64();
                }
                if (root.TryGetProperty("cost_usd", out var cu)
                    && cu.ValueKind == JsonValueKind.Number)
                {
                    cost = cu.GetDecimal();
                }
            }
            catch (JsonException)
            {
                continue; // payload corrompido não derruba o card
            }

            var key = model ?? "unknown";
            var cur = acc.GetValueOrDefault(key);
            acc[key] = (cur.Tokens + tokens, cur.Cost + cost);
        }

        return acc.Select(kv => new ModelUsage(
                kv.Key, kv.Value.Tokens, kv.Value.Cost))
            .OrderByDescending(m => m.Tokens)
            .ToList();
    }
}