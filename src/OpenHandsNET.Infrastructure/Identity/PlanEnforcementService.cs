using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Identity;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>
/// SPEC-20 RF-002/003: resolves the org's effective plan and enforces its
/// limits at conversation-create and run-start. Fail-open on bad config
/// (invalid <c>LimitsJson</c> → empty limits + warning). All checks no-op
/// when <c>Billing:EnforcementEnabled=false</c>.
/// </summary>
public sealed class PlanEnforcementService(
    AppDbContext db,
    IOptions<BillingOptions> options,
    IUsageBuffer usageBuffer,
    ILogger<PlanEnforcementService> logger) : IPlanEnforcementService
{
    /// <summary>Fallback plan name when no subscription/PlanId applies.</summary>
    public const string FreePlanName = "free";

    /// <inheritdoc />
    public async Task<EffectivePlan> GetEffectivePlanAsync(
        Guid? orgId, CancellationToken ct)
    {
        PlanRecord? plan = null;
        if (orgId is { } oid)
        {
            // Subscrição ativa mais recente tem precedência sobre org.PlanId.
            // EndedAt/StartedAt são DateTimeOffset — sem coluna Ticks, o
            // filtro temporal roda em memória (poucas subs por org).
            var now = DateTimeOffset.UtcNow;
            var subs = await db.Subscriptions.AsNoTracking()
                .Where(s => s.OrgId == oid && s.Status == "active")
                .ToListAsync(ct);
            var subPlanId = subs
                .Where(s => s.EndedAt is null || s.EndedAt > now)
                .OrderByDescending(s => s.StartedAt)
                .Select(s => (Guid?)s.PlanId)
                .FirstOrDefault();
            var orgPlanId = subPlanId ?? await db.Organizations.AsNoTracking()
                .Where(o => o.Id == oid)
                .Select(o => o.PlanId)
                .FirstOrDefaultAsync(ct);
            if (orgPlanId is { } pid)
            {
                plan = await db.Plans.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == pid, ct);
            }
        }

        plan ??= await db.Plans.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Name == FreePlanName, ct);
        if (plan is null)
        {
            return new EffectivePlan(null, FreePlanName, PlanLimits.Empty);
        }

        var limits = PlanLimits.Parse(plan.LimitsJson);
        if (plan.LimitsJson is { Length: > 0 } json
            && limits == PlanLimits.Empty && json.Trim() != "{}")
        {
            logger.LogWarning(
                "Plan {PlanId} has unparseable LimitsJson — treating as unlimited",
                plan.Id);
        }

        return new EffectivePlan(plan.Id, plan.Name, limits);
    }

    /// <inheritdoc />
    public async Task<EffectivePlan> GetEffectiveLimitsAsync(
        Guid? orgId, Guid? userId, CancellationToken ct)
    {
        var plan = await GetEffectivePlanAsync(orgId, ct);
        if (orgId is not { } oid || userId is not { } uid)
        {
            return plan;
        }

        // 1) override manual (admin) — não expirado.
        var now = DateTimeOffset.UtcNow;
        var overrides = await db.UserUsageOverrides.AsNoTracking()
            .Where(o => o.OrgId == oid && o.UserId == uid)
            .ToListAsync(ct);
        var active = overrides
            .Where(o => o.ExpiresAt is null || o.ExpiresAt > now)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefault();
        if (active is { LimitsJson.Length: > 0 } ovr)
        {
            return plan with
            {
                Name = "override",
                Limits = PlanLimits.Parse(ovr.LimitsJson),
            };
        }

        // 2) tier via IdP group mapping gravado na membership.
        var tierId = await db.OrgMembers.AsNoTracking()
            .Where(m => m.OrgId == oid && m.UserId == uid)
            .Select(m => m.UsageTierId)
            .FirstOrDefaultAsync(ct);
        if (tierId is { } tid)
        {
            var tierJson = await db.UsageTiers.AsNoTracking()
                .Where(t => t.Id == tid)
                .Select(t => t.LimitsJson)
                .FirstOrDefaultAsync(ct);
            if (tierJson is { Length: > 0 })
            {
                return plan with
                {
                    Name = "tier",
                    Limits = PlanLimits.Parse(tierJson),
                };
            }
        }

        // 3) plano da org.
        return plan;
    }

    /// <inheritdoc cref="IPlanEnforcementService.CheckConversationCreateAsync(Guid?, CancellationToken)" />
    public Task<PlanLimitError?> CheckConversationCreateAsync(
        Guid? orgId, CancellationToken ct) =>
        CheckConversationCreateAsync(orgId, null, ct);

    /// <inheritdoc />
    public async Task<PlanLimitError?> CheckConversationCreateAsync(
        Guid? orgId, Guid? userId, CancellationToken ct)
    {
        if (!options.Value.EnforcementEnabled)
        {
            return null;
        }

        var plan = await GetEffectiveLimitsAsync(orgId, userId, ct);
        if (plan.Limits.MaxConversationsPerDay is not { } daily)
        {
            return null;
        }

        var todayStart = new DateTimeOffset(
            DateTime.UtcNow.Date, TimeSpan.Zero).UtcTicks;
        var used = await db.Conversations.AsNoTracking()
            .CountAsync(c => c.OrgId == orgId
                && c.CreatedAtTicks >= todayStart
                && c.ExecutionStatus
                    != nameof(Domain.Conversations.ConversationExecutionStatus.Archived),
                ct);
        return used >= daily
            ? new PlanLimitError("max_conversations_per_day", daily, used)
            : null;
    }

    /// <inheritdoc cref="IPlanEnforcementService.CheckRunStartAsync(Guid?, CancellationToken)" />
    public Task<PlanLimitError?> CheckRunStartAsync(
        Guid? orgId, CancellationToken ct) =>
        CheckRunStartAsync(orgId, null, ct);

    /// <inheritdoc />
    public async Task<PlanLimitError?> CheckRunStartAsync(
        Guid? orgId, Guid? userId, CancellationToken ct)
    {
        if (!options.Value.EnforcementEnabled)
        {
            return null;
        }

        var plan = await GetEffectiveLimitsAsync(orgId, userId, ct);
        var limits = plan.Limits;

        if (limits.MaxConcurrentRuns is { } maxConcurrent)
        {
            var running = await (
                from s in db.Sandboxes.AsNoTracking()
                join c in db.Conversations.AsNoTracking()
                    on s.ConversationId equals (Guid?)c.Id
                where c.OrgId == orgId
                select s.Status)
                .CountAsync(st => st == nameof(SandboxStatus.Running)
                    || st == nameof(SandboxStatus.Starting), ct);
            if (running >= maxConcurrent)
            {
                return new PlanLimitError(
                    "max_concurrent_runs", maxConcurrent, running);
            }
        }

        var period = DateTimeOffset.UtcNow.ToString("yyyy-MM");
        if (limits.MaxTokensPerMonth is { } maxTokens)
        {
            var tokens = await db.UsageRecords.AsNoTracking()
                .Where(u => u.OrgId == orgId && u.Period == period)
                .SumAsync(u => (long?)u.TokensUsed, ct) ?? 0;
            // SPEC-36: soma o buffer não-flushado — com write-path
            // bufferado o EF atrasa até 30s e o limite furaria.
            if (orgId is { } o)
            {
                tokens += (await usageBuffer.GetPendingAsync(
                    o, period, ct)).Tokens;
            }

            if (tokens >= maxTokens)
            {
                return new PlanLimitError(
                    "max_tokens_per_month", maxTokens, tokens);
            }
        }

        if (limits.MaxSandboxSecondsPerMonth is { } maxSeconds)
        {
            var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            var periodStart = new DateTimeOffset(
                DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, 1,
                0, 0, 0, TimeSpan.Zero).UtcTicks;
            var used = await (
                from s in db.Sandboxes.AsNoTracking()
                join c in db.Conversations.AsNoTracking()
                    on s.ConversationId equals (Guid?)c.Id
                where c.OrgId == orgId && s.CreatedAtTicks < nowTicks
                select new
                {
                    s.Status,
                    s.CreatedAtTicks,
                    ExpiresTicks = s.ExpiresAtTicks ?? nowTicks,
                }).ToListAsync(ct);
            var seconds = (long)used.Sum(s =>
                Math.Max(0, Math.Min(s.ExpiresTicks, nowTicks)
                    - Math.Max(s.CreatedAtTicks, periodStart))
                    / (double)TimeSpan.TicksPerSecond);
            if (seconds >= maxSeconds)
            {
                return new PlanLimitError(
                    "max_sandbox_seconds_per_month", maxSeconds, seconds);
            }
        }

        return null;
    }
}