using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Coordination;
using Sandcastle.Application.Scans;

namespace Sandcastle.Infrastructure.Scans;

/// <summary>
/// SPEC-143 RF-002 — fila in-process de scans (mesmo padrão do
/// <c>WikiDispatcher</c>): singleton com channel ilimitado; o
/// <see cref="ScanWorker"/> drena criando scope por scan.
/// </summary>
public sealed class ScanDispatcher : IScanDispatcher
{
    private readonly Channel<Guid> _queue =
        Channel.CreateUnbounded<Guid>(
            new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Queue drained by <see cref="ScanWorker"/>.</summary>
    internal ChannelReader<Guid> Queue => _queue.Reader;

    /// <inheritdoc />
    public ValueTask EnqueueAsync(Guid scanId, CancellationToken ct) =>
        _queue.Writer.WriteAsync(scanId, ct);
}

/// <summary>
/// Worker de scans (SPEC-143 RF-002/RF-003): drena a fila e executa cada
/// scan via <see cref="IScanOrchestrator"/> em scope próprio (DbContext
/// scoped). Também reconcilia o pipeline de remediação
/// (<c>assigned → pr_open → merged</c>) num tick periódico com lock
/// distribuído — idempotente, multi-réplica safe.
/// </summary>
public sealed class ScanWorker(
    ScanDispatcher dispatcher,
    IServiceScopeFactory scopes,
    IDistributedLock distributedLock,
    ILogger<ScanWorker> logger) : BackgroundService
{
    /// <summary>Cadência do reconcile de remediação.</summary>
    private static readonly TimeSpan ReconcileEvery =
        TimeSpan.FromMinutes(2);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Boot grace — DatabaseMigrator precisa subir o schema antes
        // do primeiro SELECT (mesmo padrão do AutomationScheduler).
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        var nextReconcile = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            Guid scanId;
            try
            {
                var wait = dispatcher.Queue.WaitToReadAsync(stoppingToken);
                var delay = Task.Delay(
                    TimeSpan.FromSeconds(5), stoppingToken);
                var done = await Task.WhenAny(wait.AsTask(), delay);
                if (done == wait.AsTask() && wait.Result)
                {
                    scanId = await dispatcher.Queue.ReadAsync(stoppingToken);
                    await RunScanAsync(scanId, stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (DateTimeOffset.UtcNow >= nextReconcile)
            {
                nextReconcile = DateTimeOffset.UtcNow + ReconcileEvery;
                await ReconcileAsync(stoppingToken);
            }
        }
    }

    private async Task RunScanAsync(Guid scanId, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var orchestrator = scope.ServiceProvider
                .GetRequiredService<IScanOrchestrator>();
            await orchestrator.RunAsync(scanId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "scan {ScanId} worker failed", scanId);
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        try
        {
            // Um reconcile por vez entre réplicas — perdedor espera o
            // próximo tick (mesmo padrão do AutomationScheduler).
            await using var guard = await distributedLock.TryAcquireAsync(
                "scan-remediation-reconcile",
                ReconcileEvery + TimeSpan.FromSeconds(30), ct);
            if (guard is null)
            {
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var svc = scope.ServiceProvider
                .GetRequiredService<IScanService>();
            await svc.ReconcileRemediationsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "scan remediation reconcile failed");
        }
    }
}