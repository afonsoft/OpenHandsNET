using System.Threading.Channels;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Coordination;
using Sandcastle.Application.Wiki;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Wiki;

/// <summary>
/// SPEC-145 RF-002 — fila in-process de runs de wiki (mesmo padrão do
/// <c>ReviewDispatcher</c>): singleton com channel ilimitado; o
/// <see cref="WikiRunWorker"/> drena criando scope por run.
/// </summary>
public sealed class WikiDispatcher : IWikiDispatcher
{
    private readonly Channel<Guid> _queue =
        Channel.CreateUnbounded<Guid>(
            new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Queue drained by <see cref="WikiRunWorker"/>.</summary>
    internal ChannelReader<Guid> Queue => _queue.Reader;

    /// <inheritdoc />
    public ValueTask EnqueueAsync(Guid wikiRunId, CancellationToken ct) =>
        _queue.Writer.WriteAsync(wikiRunId, ct);
}

/// <summary>
/// Worker da wiki (SPEC-145 RF-002): drena a fila e executa cada run via
/// <see cref="IWikiRunner"/> em scope próprio (DbContext scoped).
/// </summary>
public sealed class WikiRunWorker(
    WikiDispatcher dispatcher,
    IServiceScopeFactory scopes,
    IDistributedLock distributedLock,
    ILogger<WikiRunWorker> logger) : BackgroundService
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
                    .GetRequiredService<IWikiRunner>();
                await runner.RunAsync(runId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "wiki run {RunId} worker failed", runId);
            }
        }
    }

    /// <summary>
    /// Reconcile de boot (mesmo padrão dos workers SCM): a fila é
    /// in-process, então no start todo run <c>queued</c> é órfão do
    /// processo anterior e volta ao canal; runs interrompidos
    /// (<c>running</c>) viram <c>failed</c> com <c>worker_restart</c>.
    /// </summary>
    private async Task ReconcileOrphansAsync(CancellationToken ct)
    {
        try
        {
            // Um reconcile por vez entre réplicas (padrão ScanWorker).
            await using var guard = await distributedLock.TryAcquireAsync(
                "wiki-orphan-reconcile", TimeSpan.FromMinutes(2), ct);
            if (guard is null)
            {
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider
                .GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            var interrupted = await db.WikiRuns.IgnoreQueryFilters()
                .Where(r => r.Status == WikiRunStatuses.Running)
                .ToListAsync(ct);
            foreach (var run in interrupted)
            {
                run.Status = WikiRunStatuses.Failed;
                run.Error = "worker_restart";
                run.FinishedAt ??= now;
            }
            if (interrupted.Count > 0)
            {
                await db.SaveChangesAsync(ct);
            }

            var queued = await db.WikiRuns.IgnoreQueryFilters()
                .Where(r => r.Status == WikiRunStatuses.Queued)
                .Select(r => r.Id)
                .ToListAsync(ct);
            foreach (var id in queued)
            {
                await dispatcher.EnqueueAsync(id, ct);
            }

            if (queued.Count + interrupted.Count > 0)
            {
                logger.LogInformation(
                    "wiki orphan reconcile: {Queued} requeued, "
                    + "{Failed} failed (worker_restart)",
                    queued.Count, interrupted.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reconcile falhou não pode derrubar o drain da fila.
            logger.LogWarning(ex, "wiki orphan reconcile failed");
        }
    }
}