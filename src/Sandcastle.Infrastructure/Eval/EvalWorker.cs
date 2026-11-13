using System.Threading.Channels;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Eval;

namespace Sandcastle.Infrastructure.Eval;

/// <summary>
/// SPEC-152 RF-002 — fila in-process de eval runs (mesmo padrão do
/// <c>RefactorDispatcher</c>): singleton com channel ilimitado de
/// run ids; o <see cref="EvalWorker"/> drena criando scope por run.
/// </summary>
public sealed class EvalDispatcher : IEvalDispatcher
{
    private readonly Channel<Guid> _queue =
        Channel.CreateUnbounded<Guid>(
            new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Queue drained by <see cref="EvalWorker"/>.</summary>
    internal ChannelReader<Guid> Queue => _queue.Reader;

    /// <inheritdoc />
    public ValueTask EnqueueAsync(Guid runId, CancellationToken ct) =>
        _queue.Writer.WriteAsync(runId, ct);
}

/// <summary>
/// Worker de evals (SPEC-152): drena a fila →
/// <see cref="IEvalRunner"/> em scope próprio por run. No boot,
/// re-enfileira runs presas em <c>queued</c> (restart-safe). Runs
/// órfãs em <c>running</c> NÃO são tocadas — multi-réplica: podem
/// pertencer a outra réplica; o usuário cancela via API.
/// </summary>
public sealed class EvalWorker(
    EvalDispatcher dispatcher,
    IServiceScopeFactory scopes,
    ILogger<EvalWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        // Boot grace — DatabaseMigrator precisa subir o schema antes
        // do primeiro SELECT (mesmo padrão dos demais workers).
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        await SweepOnBootAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            Guid runId;
            try
            {
                runId = await dispatcher.Queue
                    .ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider
                    .GetRequiredService<IEvalRunner>()
                    .RunAsync(runId, stoppingToken);
            }
            catch (Exception ex)
                when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "eval run {RunId} worker failed", runId);
            }
        }
    }

    /// <summary>Restart-safe sweep: queued → requeue; running → failed.</summary>
    private async Task SweepOnBootAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider
                .GetRequiredService<Persistence.AppDbContext>();
            var queued = await db.EvalRuns.IgnoreQueryFilters()
                .Where(r => r.Status == EvalRunStatuses.Queued)
                .Select(r => r.Id).ToListAsync(ct);
            foreach (var id in queued)
            {
                await dispatcher.EnqueueAsync(id, ct);
            }

            if (queued.Count > 0)
            {
                logger.LogInformation(
                    "eval boot sweep requeued {Count} runs",
                    queued.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "eval boot sweep failed");
        }
    }
}