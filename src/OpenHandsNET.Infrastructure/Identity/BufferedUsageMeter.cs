using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Coordination;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>
/// SPEC-36 RF-002: <see cref="IUsageMeter"/> que grava no
/// <see cref="IUsageBuffer"/> (Redis hash atômico) em vez do EF — o
/// <see cref="UsageFlushWorker"/> drena p/ <c>usage_records</c> a cada
/// 30s. Registrado só quando <c>Cache:Provider=redis</c>; single-réplica
/// segue no <see cref="UsageMeter"/> direto.
/// </summary>
public sealed class BufferedUsageMeter(IUsageBuffer buffer) : IUsageMeter
{
    /// <inheritdoc />
    public Task RecordTokensAsync(
        Guid orgId, long tokens, decimal? costUsd, CancellationToken ct) =>
        tokens <= 0 && costUsd is null or <= 0
            ? Task.CompletedTask
            : buffer.AddAsync(orgId, tokens, costUsd, ct);
}

/// <summary>
/// SPEC-36 RF-002/004: drena o buffer Redis p/ o EF sob lock
/// <c>usage-flush</c> (uma réplica por vez — snapshot→upsert→confirm é
/// atômico por org; a confirmação decrementa exatamente o drenado, então
/// deltas concorrentes nunca se perdem).
/// </summary>
public sealed class UsageFlushWorker(
    IServiceScopeFactory scopes,
    IUsageBuffer buffer,
    IDistributedLock distributedLock,
    ILogger<UsageFlushWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
    }

    private async Task FlushOnceAsync(CancellationToken ct)
    {
        await using var guard = await distributedLock.TryAcquireAsync(
            "usage-flush", TimeSpan.FromMinutes(2), ct);
        if (guard is null || !guard.IsHeld)
        {
            return; // outra réplica drena nesta rodada
        }

        var pending = await buffer.SnapshotPendingAsync(ct);
        if (pending.Count == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var p in pending)
        {
            try
            {
                var row = await db.UsageRecords.FirstOrDefaultAsync(
                    u => u.OrgId == p.OrgId && u.Period == p.Period, ct);
                if (row is null)
                {
                    db.UsageRecords.Add(new UsageRecord
                    {
                        Id = Guid.NewGuid(),
                        OrgId = p.OrgId,
                        Period = p.Period,
                        TokensUsed = p.Tokens,
                        CostUsd = p.CostUsd > 0 ? p.CostUsd : null,
                        RecordedAt = DateTimeOffset.UtcNow,
                    });
                }
                else
                {
                    row.TokensUsed += p.Tokens;
                    row.CostUsd = (row.CostUsd ?? 0m) + p.CostUsd;
                    row.RecordedAt = DateTimeOffset.UtcNow;
                }

                await db.SaveChangesAsync(ct);
                await buffer.ConfirmAsync(p, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Entry fica dirty → próxima rodada re-tenta.
                logger.LogWarning(e,
                    "usage flush failed {Org}/{Period}", p.OrgId, p.Period);
            }
        }
    }
}