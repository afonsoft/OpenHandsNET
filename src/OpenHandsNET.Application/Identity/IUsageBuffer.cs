namespace OpenHandsNET.Application.Identity;

/// <summary>Delta de uso pendente de flush para EF.</summary>
public sealed record PendingUsage(
    Guid OrgId, string Period, long Tokens, decimal CostUsd);

/// <summary>
/// SPEC-36 RF-002: write-path de usage metering — buffer Redis
/// (<c>ohn:usage:{org}:{period}</c> hash tokens/cost_cents + set dirty)
/// drenado para EF pelo flush worker; reduz write churn e dá quota
/// cross-réplica em tempo real. <see cref="NullUsageBuffer"/> em
/// single-replica (EF direto, pendente = 0).
/// </summary>
public interface IUsageBuffer
{
    /// <summary>Acumula tokens/custo do período corrente da org.</summary>
    Task AddAsync(
        Guid orgId, long tokens, decimal? costUsd, CancellationToken ct);

    /// <summary>Pendente ainda não persistido (somado ao EF nas checagens).</summary>
    Task<PendingUsage> GetPendingAsync(
        Guid orgId, string period, CancellationToken ct);

    /// <summary>Snapshot das chaves dirty para o flush worker.</summary>
    Task<IReadOnlyList<PendingUsage>> SnapshotPendingAsync(CancellationToken ct);

    /// <summary>Subtrai o que o EF persistiu (confirm-atômica pós-write).</summary>
    Task ConfirmAsync(PendingUsage drained, CancellationToken ct);
}

/// <summary>Single-replica: sem buffer — EF é a fonte direta.</summary>
public sealed class NullUsageBuffer : IUsageBuffer
{
    /// <inheritdoc />
    public Task AddAsync(
        Guid orgId, long tokens, decimal? costUsd, CancellationToken ct) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task<PendingUsage> GetPendingAsync(
        Guid orgId, string period, CancellationToken ct) =>
        Task.FromResult(new PendingUsage(orgId, period, 0, 0m));

    /// <inheritdoc />
    public Task<IReadOnlyList<PendingUsage>> SnapshotPendingAsync(
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PendingUsage>>([]);

    /// <inheritdoc />
    public Task ConfirmAsync(PendingUsage drained, CancellationToken ct) =>
        Task.CompletedTask;
}