namespace OpenHandsNET.Application.Identity;

/// <summary>
/// Aggregated org usage (SPEC-14 RF-005): usage_records tokens/cost plus
/// live sandbox-second and run counters derived from the runtime tables.
/// </summary>
public interface IUsageService
{
    /// <summary>Aggregate the current-period usage for an org.</summary>
    Task<OrgUsage> GetOrgUsageAsync(Guid orgId, CancellationToken ct);
}

/// <summary>Org usage snapshot for the current calendar month.</summary>
public sealed record OrgUsage(
    Guid OrgId,
    string Period,
    long TokensUsed,
    decimal? CostUsd,
    double SandboxSeconds,
    int Runs,
    int SandboxesActive);