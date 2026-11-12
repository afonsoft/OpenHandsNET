using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Coordination;
using Sandcastle.Application.Refactors;

namespace Sandcastle.Infrastructure.Refactors;

/// <summary>
/// SPEC-144 RF-001/RF-002 — fila in-process de trabalho de refactors
/// (mesmo padrão do <c>ScanDispatcher</c>): singleton com channel
/// ilimitado de <see cref="RefactorWorkItem"/>; o
/// <see cref="RefactorWorker"/> drena criando scope por item.
/// </summary>
public sealed class RefactorDispatcher : IRefactorDispatcher
{
    private readonly Channel<RefactorWorkItem> _queue =
        Channel.CreateUnbounded<RefactorWorkItem>(
            new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Queue drained by <see cref="RefactorWorker"/>.</summary>
    internal ChannelReader<RefactorWorkItem> Queue => _queue.Reader;

    /// <inheritdoc />
    public ValueTask EnqueueAsync(Guid planId, RefactorWorkKind kind,
        CancellationToken ct) =>
        _queue.Writer.WriteAsync(new(planId, kind), ct);
}

/// <summary>One queued unit of refactor work.</summary>
public readonly record struct RefactorWorkItem(
    Guid PlanId, RefactorWorkKind Kind);

/// <summary>
/// Worker de refactors (SPEC-144): drena a fila — <c>Plan</c> →
/// <see cref="IRefactorPlanner"/> (grafo → lotes), <c>Execute</c> →
/// <see cref="IBatchExecutor"/> (deps → dispatch → coleta) — em scope
/// próprio por item. Também reconcilia PRs abertos
/// (<c>pr_open → merged|failed</c>) num tick periódico com lock
/// distribuído — idempotente, multi-réplica safe.
/// </summary>
public sealed class RefactorWorker(
    RefactorDispatcher dispatcher,
    IServiceScopeFactory scopes,
    IDistributedLock distributedLock,
    ILogger<RefactorWorker> logger) : BackgroundService
{
    /// <summary>Cadência do reconcile de PRs.</summary>
    private static readonly TimeSpan ReconcileEvery =
        TimeSpan.FromMinutes(2);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Boot grace — DatabaseMigrator precisa subir o schema antes
        // do primeiro SELECT (mesmo padrão do ScanWorker).
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        var nextReconcile = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var wait = dispatcher.Queue.WaitToReadAsync(stoppingToken);
                var delay = Task.Delay(
                    TimeSpan.FromSeconds(5), stoppingToken);
                var done = await Task.WhenAny(wait.AsTask(), delay);
                if (done == wait.AsTask() && wait.Result)
                {
                    var item = await dispatcher.Queue.ReadAsync(
                        stoppingToken);
                    await RunItemAsync(item, stoppingToken);
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

    private async Task RunItemAsync(
        RefactorWorkItem item, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (item.Kind == RefactorWorkKind.Plan)
            {
                await scope.ServiceProvider
                    .GetRequiredService<IRefactorPlanner>()
                    .RunAsync(item.PlanId, ct);
            }
            else
            {
                await scope.ServiceProvider
                    .GetRequiredService<IBatchExecutor>()
                    .RunAsync(item.PlanId, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "refactor {PlanId} worker failed ({Kind})",
                item.PlanId, item.Kind);
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        try
        {
            // Um reconcile por vez entre réplicas — perdedor espera o
            // próximo tick (mesmo padrão do ScanWorker).
            await using var guard = await distributedLock.TryAcquireAsync(
                "refactor-pr-reconcile",
                ReconcileEvery + TimeSpan.FromSeconds(30), ct);
            if (guard is null)
            {
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var svc = scope.ServiceProvider
                .GetRequiredService<IRefactorService>();
            await svc.ReconcileAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "refactor pr reconcile failed");
        }
    }
}