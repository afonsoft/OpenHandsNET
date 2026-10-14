using OpenHandsNET.Domain.Identity;

namespace OpenHandsNET.Application.Identity;

/// <summary>One exceeded plan limit (SPEC-20 RF-002) — maps to 403 fields.</summary>
public sealed record PlanLimitError(string LimitKey, long Limit, long Used);

/// <summary>
/// Thrown when a plan limit blocks the requested operation — mapped to HTTP
/// 403 <c>plan_limit_exceeded</c> at the API boundary (SPEC-20 RF-003).
/// </summary>
public sealed class PlanLimitExceededException(PlanLimitError error)
    : InvalidOperationException(
        $"plan_limit_exceeded:{error.LimitKey} ({error.Used}/{error.Limit})")
{
    /// <summary>The violated limit key and counters.</summary>
    public PlanLimitError Error { get; } = error;
}

/// <summary>Effective plan resolution for an org (or the free default).</summary>
public sealed record EffectivePlan(Guid? PlanId, string Name, PlanLimits Limits);

/// <summary>
/// Plan enforcement (SPEC-20 RF-002/003). All checks return
/// <see langword="null"/> when <c>Billing:EnforcementEnabled=false</c>
/// (OSS self-host is never blocked).
/// </summary>
public interface IPlanEnforcementService
{
    /// <summary>Plan limits of the org: active subscription → org.PlanId → free.</summary>
    Task<EffectivePlan> GetEffectivePlanAsync(
        Guid? orgId, CancellationToken cancellationToken);

    /// <summary>Checks <c>max_conversations_per_day</c> before create.</summary>
    Task<PlanLimitError?> CheckConversationCreateAsync(
        Guid? orgId, CancellationToken cancellationToken);

    /// <summary>
    /// Checks <c>max_concurrent_runs</c>, <c>max_tokens_per_month</c> and
    /// <c>max_sandbox_seconds_per_month</c> before a sandbox start.
    /// </summary>
    Task<PlanLimitError?> CheckRunStartAsync(
        Guid? orgId, CancellationToken cancellationToken);
}
