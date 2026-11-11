using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Scm;

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
    ILogger<QaRunWorker> logger) : BackgroundService
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
                    .GetRequiredService<IQaRunner>();
                await runner.RunAsync(runId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "qa run {RunId} worker failed", runId);
            }
        }
    }
}