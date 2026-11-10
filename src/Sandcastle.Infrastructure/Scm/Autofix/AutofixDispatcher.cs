using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Scm;

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
    ILogger<AutofixRunWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var inFlight = new List<Task>();
        await foreach (var runId in dispatcher.Queue
            .ReadAllAsync(stoppingToken))
        {
            inFlight.RemoveAll(t => t.IsCompleted);
            inFlight.Add(RunScopedAsync(runId, stoppingToken));
        }
        await Task.WhenAll(inFlight);
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