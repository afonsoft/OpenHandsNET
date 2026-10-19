using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Automations;
using OpenHandsNET.Application.Coordination;

namespace OpenHandsNET.Infrastructure.Automations;

/// <summary>
/// SPEC-47 RF-002: tick 30s — dispara automations devidas e reconcilia
/// runs <c>running</c>. Lock distribuído por tick para multi-réplica
/// (mesmo padrão do <see cref="Sandbox.OrphanSweepService"/>); falha de
/// um tick não derruba o serviço.
/// </summary>
public sealed class AutomationScheduler(
    IServiceScopeFactory scopes,
    IDistributedLock distributedLock,
    ILogger<AutomationScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Boot grace — o DatabaseMigrator precisa subir o schema antes
        // do primeiro SELECT (migrations rodam em hosted service também).
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "automation tick failed");
            }

            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task TickOnceAsync(CancellationToken ct)
    {
        // Um tick por vez entre réplicas — o vencedor dispara e
        // reconcilia; perdedor espera o próximo tick.
        await using var guard = await distributedLock.TryAcquireAsync(
            "automation-scheduler", Tick + TimeSpan.FromSeconds(10), ct);
        if (guard is null)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var svc = scope.ServiceProvider
            .GetRequiredService<IAutomationService>();
        await svc.TickAsync(DateTimeOffset.UtcNow, ct);
    }
}