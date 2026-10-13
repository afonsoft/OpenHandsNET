using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenHandsNET.Infrastructure.Persistence;

/// <summary>
/// Applies pending EF Core migrations at application startup.
/// </summary>
public sealed class DatabaseMigrator(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseMigrator> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
        var pendingList = pending.ToList();
        // Sempre migrations (SPEC-10: EnsureCreated proibido em produção) —
        // MigrateAsync cria a base quando ela não existe.
        logger.LogInformation(
            "Applying {Count} pending migration(s)", pendingList.Count);
        await db.Database.MigrateAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
