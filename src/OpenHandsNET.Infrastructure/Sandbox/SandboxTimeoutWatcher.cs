using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Sandbox;

/// <summary>
/// Pauses sandboxes past their <c>TimeoutMinutes</c> expiry
/// (<see cref="SandboxRecord.ExpiresAt"/>), upstream "rate limit pauses" parity.
/// </summary>
public sealed class SandboxTimeoutWatcher(
    IServiceScopeFactory scopes, ILogger<SandboxTimeoutWatcher> logger) : BackgroundService
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

        var now = DateTimeOffset.UtcNow;
        var expired = await db.Sandboxes
            .Where(r => r.Status == "Running" && r.ExpiresAt < now)
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
    }
}
