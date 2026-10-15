using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Coordination;
using OpenHandsNET.Application.Sandbox;

namespace OpenHandsNET.Infrastructure.Sandbox;

/// <summary>
/// Boot-time sweep: removes managed engine containers whose sandbox records
/// are gone (crash recovery), labeled <c>openhandsnet.managed=true</c>.
/// SPEC-35: provider-agnostic — vai pelo <see cref="ISandboxService"/>.
/// </summary>
public sealed class OrphanSweepService(
    IServiceScopeFactory scopes,
    IDistributedLock distributedLock,
    ILogger<OrphanSweepService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        try
        {
            // SPEC-36 RF-002: sweep único entre réplicas (SET NX PX).
            await using var guard = await distributedLock.TryAcquireAsync(
                "orphan-sweep", TimeSpan.FromMinutes(5), stoppingToken);
            if (guard is null)
            {
                logger.LogDebug("orphan sweep skipped — held by other replica");
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var svc = scope.ServiceProvider
                .GetRequiredService<ISandboxService>();
            await svc.SweepOrphansAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Engine indisponível (ex.: dev sem daemon) não deve derrubar o boot.
            logger.LogWarning(ex, "Orphan sweep skipped (engine unavailable?)");
        }
    }
}
