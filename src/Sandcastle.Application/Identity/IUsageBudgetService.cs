namespace Sandcastle.Application.Identity;

/// <summary>Per-model token/cost breakdown entry.</summary>
public sealed record ModelUsage(string Model, long Tokens, decimal CostUsd);

/// <summary>
/// SPEC-147 RF-004: budget snapshot for the "Your Budget" card —
/// monthly token limit (null = unlimited), tokens consumed in the
/// current calendar period, remaining (null when unlimited), period
/// bounds (UTC) and a per-model breakdown over <c>TokenEvent</c>s.
/// </summary>
public sealed record UsageBudget(
    long? MonthlyLimit,
    long Consumed,
    long? Remaining,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    IReadOnlyList<ModelUsage> ByModel);

/// <summary>
/// Computes the caller/org budget from existing usage data
/// (<c>usage_records</c> for org totals, <c>TokenEvent</c> payloads for
/// the per-model breakdown and the caller-scoped path).
/// </summary>
public interface IUsageBudgetService
{
    /// <summary>
    /// Budget for <paramref name="userId"/>. When
    /// <paramref name="orgScope"/> is true, aggregates the whole
    /// <paramref name="orgId"/> (requires <paramref name="orgId"/>;
    /// permission check is the endpoint's job). Otherwise the caller
    /// scope: own conversations + effective plan limits (user → tier →
    /// plan chain via <see cref="IPlanEnforcementService"/>).
    /// </summary>
    Task<UsageBudget> GetBudgetAsync(
        Guid userId, Guid? orgId, bool orgScope, CancellationToken ct);

    /// <summary>
    /// SPEC-149 RF-004: <c>GET /usage/llm?by=role</c> — role×model
    /// breakdown over <c>TokenEvent</c> payloads of the current period
    /// (mesma fonte do <see cref="UsageBudget.ByModel"/> — sem schema
    /// novo). Org scope exige <paramref name="orgId"/>; a checagem de
    /// permissão é do endpoint.
    /// </summary>
    Task<Agent.LlmUsageBreakdown> GetLlmUsageAsync(
        Guid userId, Guid? orgId, bool orgScope, CancellationToken ct);
}