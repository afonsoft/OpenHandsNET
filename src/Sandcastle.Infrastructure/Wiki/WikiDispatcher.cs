using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Wiki;

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
    ILogger<WikiRunWorker> logger) : BackgroundService
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
                    .GetRequiredService<IWikiRunner>();
                await runner.RunAsync(runId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "wiki run {RunId} worker failed", runId);
            }
        }
    }
}