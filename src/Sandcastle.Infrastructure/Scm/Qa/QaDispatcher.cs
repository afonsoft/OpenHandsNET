using System.Threading.Channels;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Coordination;
using Sandcastle.Application.Scm;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Scm.Qa;

/// <summary>
/// SPEC-142 RF-001 — fila in-process de runs de QA (mesmo padrão do
/// <c>ReviewDispatcher</c>/<c>AutofixDispatcher</c>): singleton com
/// channel ilimitado; o <see cref="QaRunWorker"/> drena criando scope
/// por run.
/// </summary>
public sealed class QaDispatcher : IQaDispatcher
{
    private readonly Channel<Guid> _queue =
        Channel.CreateUnbounded<Guid>(
            new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Queue drained by <see cref="QaRunWorker"/>.</summary>
    internal ChannelReader<Guid> Queue => _queue.Reader;

    /// <inheritdoc />
    public ValueTask EnqueueAsync(Guid qaRunId, CancellationToken ct) =>
        _queue.Writer.WriteAsync(qaRunId, ct);
}

/// <summary>
/// Worker do QA agent (SPEC-142 RF-002): drena a fila e executa cada
/// run via <see cref="IQaRunner"/> em scope próprio (DbContext scoped).
/// </summary>
public sealed class QaRunWorker(
    QaDispatcher dispatcher,
    IServiceScopeFactory scopes,
    IDistributedLock distributedLock,
    ILogger<QaRunWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileOrphansAsync(stoppingToken);
        await foreach (var runId in dispatcher.Queue
            .ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var runner = scope.ServiceProvider
                    .GetRequiredService<IQaRunner>();
                await runner.RunAsync(runId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "qa run {RunId} worker failed", runId);
            }
        }
    }

    /// <summary>
    /// Reconcile de boot (deferral SPEC-142 fechado): a fila é in-process,
    /// então no start todo run <c>queued</c> é órfão do processo anterior
    /// e volta ao canal; runs interrompidos (<c>running</c>) viram
    /// <c>failed</c> com <c>worker_restart</c> — re-executar cego podia
    /// duplicar efeitos externos (comentário/status já postados antes do
    /// crash) e <c>failed</c> sai da dedup, liberando retrigger.
    /// </summary>
    private async Task ReconcileOrphansAsync(CancellationToken ct)
    {
        try
        {
            // Um reconcile por vez entre réplicas (padrão ScanWorker).
            await using var guard = await distributedLock.TryAcquireAsync(
                "qa-orphan-reconcile", TimeSpan.FromMinutes(2), ct);
            if (guard is null)
            {
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider
                .GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            var interrupted = await db.QaRuns.IgnoreQueryFilters()
                .Where(r => r.Status == QaRunStatuses.Running)
                .ToListAsync(ct);
            foreach (var run in interrupted)
            {
                run.Status = QaRunStatuses.Failed;
                run.Error = "worker_restart";
                run.FinishedAt ??= now;
            }
            if (interrupted.Count > 0)
            {
                await db.SaveChangesAsync(ct);
            }

            var queued = await db.QaRuns.IgnoreQueryFilters()
                .Where(r => r.Status == QaRunStatuses.Queued)
                .Select(r => r.Id)
                .ToListAsync(ct);
            foreach (var id in queued)
            {
                await dispatcher.EnqueueAsync(id, ct);
            }

            if (queued.Count + interrupted.Count > 0)
            {
                logger.LogInformation(
                    "qa orphan reconcile: {Queued} requeued, "
                    + "{Failed} failed (worker_restart)",
                    queued.Count, interrupted.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reconcile falhou não pode derrubar o drain da fila.
            logger.LogWarning(ex, "qa orphan reconcile failed");
        }
    }
}