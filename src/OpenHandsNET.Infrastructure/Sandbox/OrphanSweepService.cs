using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenHandsNET.Infrastructure.Sandbox;

/// <summary>
/// Boot-time sweep: removes managed containers whose sandbox records are gone
/// (crash recovery), labeled <c>openhandsnet.managed=true</c>.
/// </summary>
public sealed class OrphanSweepService(
    IServiceScopeFactory scopes, ILogger<OrphanSweepService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var svc = (DockerSandboxService)
                scope.ServiceProvider.GetRequiredService(
                    typeof(OpenHandsNET.Application.Sandbox.ISandboxService));
            await svc.SweepOrphansAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Docker indisponível (ex.: dev sem daemon) não deve derrubar o boot.
            logger.LogWarning(ex, "Orphan sweep skipped (Docker unavailable?)");
        }
    }
}
