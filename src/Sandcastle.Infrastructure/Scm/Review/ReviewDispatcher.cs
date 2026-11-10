using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Scm;

namespace Sandcastle.Infrastructure.Scm.Review;

/// <summary>
/// SPEC-140 RF-002 — fila in-process de runs de review (mesmo padrão do
/// <c>StartTaskService</c>): singleton com channel ilimitado; o
/// <see cref="ReviewRunWorker"/> drena criando scope por run.
/// </summary>
public sealed class ReviewDispatcher : IReviewDispatcher
{
    private readonly Channel<Guid> _queue =
        Channel.CreateUnbounded<Guid>(
            new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Queue drained by <see cref="ReviewRunWorker"/>.</summary>
    internal ChannelReader<Guid> Queue => _queue.Reader;

    /// <inheritdoc />
    public ValueTask EnqueueAsync(Guid reviewRunId, CancellationToken ct) =>
        _queue.Writer.WriteAsync(reviewRunId, ct);
}

/// <summary>
/// Worker do auto-review (SPEC-140 RF-002): drena a fila e executa cada
/// run via <see cref="IReviewRunner"/> em scope próprio (DbContext scoped).
/// </summary>
public sealed class ReviewRunWorker(
    ReviewDispatcher dispatcher,
    IServiceScopeFactory scopes,
    ILogger<ReviewRunWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var runId in dispatcher.Queue
            .ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var runner = scope.ServiceProvider
                    .GetRequiredService<IReviewRunner>();
                await runner.RunAsync(runId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "review run {RunId} worker failed", runId);
            }
        }
    }
}