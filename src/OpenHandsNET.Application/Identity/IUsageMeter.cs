namespace OpenHandsNET.Application.Identity;

/// <summary>
/// Metering write-path (SPEC-20 RF-004): aggregates LLM token usage into
/// <c>usage_records</c> keyed by org + period (<c>yyyy-MM</c>). Sandbox
/// seconds and run counts are derived live from runtime tables by
/// <see cref="IUsageService"/> — no separate write is needed.
/// </summary>
public interface IUsageMeter
{
    /// <summary>Adds prompt+completion tokens (and optional cost) to the org's current period row.</summary>
    Task RecordTokensAsync(
        Guid orgId, long tokens, decimal? costUsd, CancellationToken ct);
}
