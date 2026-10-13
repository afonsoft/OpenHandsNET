using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Skills;

/// <summary>Runs <see cref="BuiltinSkillSeeder"/> once at boot, after migrations.</summary>
public sealed class BuiltinSkillSeedService(
    IServiceScopeFactory scopes,
    ILogger<BuiltinSkillSeedService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Aguarda o DatabaseMigrator aplicar o schema (retry até ~60s).
        for (var attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await BuiltinSkillSeeder.SeedAsync(db, logger, stoppingToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt == 119)
                {
                    logger.LogError(ex, "Builtin skill seeding failed");
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
            }
        }
    }
}
