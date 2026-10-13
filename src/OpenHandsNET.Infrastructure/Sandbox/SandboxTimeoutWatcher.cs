using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Sandbox;

/// <summary>
/// Pauses sandboxes past their <c>TimeoutMinutes</c> expiry
/// (<see cref="SandboxRecord.ExpiresAt"/>), upstream "rate limit pauses" parity.
/// SPEC-16 RF-003: também faz <c>docker stop</c> de sandboxes em
/// <c>Paused</c> há mais de <c>Sandbox:SleepStopAfterHours</c> (0 = nunca).
/// </summary>
public sealed class SandboxTimeoutWatcher(
    IServiceScopeFactory scopes,
    IOptions<SandboxOptions> options,
    ILogger<SandboxTimeoutWatcher> logger) : BackgroundService
{
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepExpiredAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Sandbox timeout sweep failed");
            }
        }
    }

    private async Task SweepExpiredAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISandboxService>();

        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var expired = await db.Sandboxes
            .Where(r => r.Status == "Running" && r.ExpiresAtTicks < nowTicks)
            .ToListAsync(ct);
        foreach (var record in expired)
        {
            logger.LogInformation(
                "Sandbox {SandboxId} expired at {ExpiresAt} — pausing",
                record.Id, record.ExpiresAt);
            try
            {
                await service.PauseAsync(record.Id, ct);
            }
            catch (Exception ex)
            {
                record.Status = Domain.Sandbox.SandboxStatus.Error.ToString();
                record.LastError = ex.Message;
                await db.SaveChangesAsync(ct);
            }
        }

        // RF-003: reaper de pausados antigos — docker stop libera RAM
        // mantendo container fs + workspace para resume posterior.
        var stopHours = options.Value.SleepStopAfterHours;
        if (stopHours > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-stopHours).UtcTicks;
            var stale = await db.Sandboxes
                .Where(r => r.Status == "Paused" && r.UpdatedAtTicks < cutoff)
                .ToListAsync(ct);
            foreach (var record in stale)
            {
                logger.LogInformation(
                    "Sandbox {SandboxId} paused for {Hours}h — stopping",
                    record.Id, stopHours);
                try
                {
                    await service.StopAsync(record.Id, ct);
                }
                catch (Exception ex)
                {
                    record.Status = Domain.Sandbox.SandboxStatus.Error.ToString();
                    record.LastError = ex.Message;
                    await db.SaveChangesAsync(ct);
                }
            }
        }
    }
}
