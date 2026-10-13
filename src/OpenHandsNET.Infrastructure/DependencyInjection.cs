using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure;

/// <summary>
/// Infrastructure service registration.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the <see cref="AppDbContext"/> with the configured provider
    /// (<c>Database:Provider</c> = <c>Sqlite</c> | <c>PostgreSql</c>) and the
    /// <see cref="DatabaseMigrator"/> startup service.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        var connectionString = configuration["Database:ConnectionString"]
            ?? "Data Source=data/openhandsnet.db";

        if (!provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            EnsureSqliteDirectory(connectionString);
        }

        services.AddDbContext<AppDbContext>(options =>
        {
            if (provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
            {
                options.UseNpgsql(connectionString);
            }
            else
            {
                options.UseSqlite(connectionString);
            }
        });
        services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);
        services.AddHostedService<DatabaseMigrator>();
        return services;
    }

    /// <summary>
    /// Creates the directory portion of a <c>Data Source=</c> SQLite connection
    /// string when it is missing (fresh checkout, new volume).
    /// </summary>
    private static void EnsureSqliteDirectory(string connectionString)
    {
        const string prefix = "Data Source=";
        var start = connectionString.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return;
        }

        var path = connectionString[(start + prefix.Length)..].Split(';')[0].Trim();
        if (string.IsNullOrEmpty(path) || path == ":memory:")
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
    }
}
