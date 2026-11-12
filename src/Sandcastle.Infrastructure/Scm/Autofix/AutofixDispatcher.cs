using System.Threading.Channels;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Coordination;
using Sandcastle.Application.Scm;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Scm.Autofix;

/// <summary>
/// SPEC-141 RF-003 — fila in-process de runs de autofix (mesmo padrão
/// do <c>ReviewDispatcher</c>): singleton com channel ilimitado; o
/// <see cref="AutofixRunWorker"/> drena criando scope por run.
/// </summary>
public sealed class AutofixDispatcher : IAutofixDispatcher
{
    private readonly Channel<Guid> _queue =
        Channel.CreateUnbounded<Guid>(
            new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Queue drained by <see cref="AutofixRunWorker"/>.</summary>
    internal ChannelReader<Guid> Queue => _queue.Reader;

    /// <inheritdoc />
    public ValueTask EnqueueAsync(Guid autofixRunId, CancellationToken ct) =>
        _queue.Writer.WriteAsync(autofixRunId, ct);
}

/// <summary>
/// Worker do CI autofix (SPEC-141 RF-003): drena a fila e dispara cada
/// run via <see cref="IAutofixRunner"/> em scope próprio. Diferente do
/// review worker, os runs são despachados em tasks paralelas — um run
/// pode pollar checks por até <c>Autofix:RunTimeoutMinutes</c> e não
/// pode bloquear a fila (dedup por PR impede corrida no mesmo PR).
/// </summary>
public sealed class AutofixRunWorker(
    AutofixDispatcher dispatcher,
    IServiceScopeFactory scopes,
    IDistributedLock distributedLock,
    ILogger<AutofixRunWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileOrphansAsync(stoppingToken);
        var inFlight = new List<Task>();
        await foreach (var runId in dispatcher.Queue
            .ReadAllAsync(stoppingToken))
        {
            inFlight.RemoveAll(t => t.IsCompleted);
            inFlight.Add(RunScopedAsync(runId, stoppingToken));
        }
        await Task.WhenAll(inFlight);
    }

    /// <summary>
    /// Reconcile de boot (deferral SPEC-141 fechado): a fila é in-process,
    /// então no start todo run <c>queued</c> é órfão do processo anterior
    /// e volta ao canal; runs interrompidos (<c>running|waiting_check</c>)
    /// viram <c>failed</c> com <c>worker_restart</c> — re-executar cego
    /// consumiria uma tentativa nova + conversa de fix duplicada (push no
    /// PR) e <c>failed</c> sai da dedup, liberando retrigger.
    /// </summary>
    private async Task ReconcileOrphansAsync(CancellationToken ct)
    {
        try
        {
            // Um reconcile por vez entre réplicas (padrão ScanWorker).
            await using var guard = await distributedLock.TryAcquireAsync(
                "autofix-orphan-reconcile", TimeSpan.FromMinutes(2), ct);
            if (guard is null)
            {
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider
                .GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            var interrupted = await db.AutofixRuns.IgnoreQueryFilters()
                .Where(r => r.Status == AutofixRunStatuses.Running
                    || r.Status == AutofixRunStatuses.WaitingCheck)
                .ToListAsync(ct);
            foreach (var run in interrupted)
            {
                run.Status = AutofixRunStatuses.Failed;
                run.Error = "worker_restart";
                run.FinishedAt ??= now;
            }
            if (interrupted.Count > 0)
            {
                await db.SaveChangesAsync(ct);
            }

            var queued = await db.AutofixRuns.IgnoreQueryFilters()
                .Where(r => r.Status == AutofixRunStatuses.Queued)
                .Select(r => r.Id)
                .ToListAsync(ct);
            foreach (var id in queued)
            {
                await dispatcher.EnqueueAsync(id, ct);
            }

            if (queued.Count + interrupted.Count > 0)
            {
                logger.LogInformation(
                    "autofix orphan reconcile: {Queued} requeued, "
                    + "{Failed} failed (worker_restart)",
                    queued.Count, interrupted.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reconcile falhou não pode derrubar o drain da fila.
            logger.LogWarning(ex, "autofix orphan reconcile failed");
        }
    }

    private async Task RunScopedAsync(Guid runId, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var runner = scope.ServiceProvider
                .GetRequiredService<IAutofixRunner>();
            await runner.RunAsync(runId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "autofix run {RunId} worker failed", runId);
        }
    }
}