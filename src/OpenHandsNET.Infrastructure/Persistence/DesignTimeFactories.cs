using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenHandsNET.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for the default (SQLite) migration set.
/// </summary>
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <inheritdoc />
    public AppDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseSqlite("Data Source=design.db")
            .UseSnakeCaseNamingConvention();
        return new AppDbContext(builder.Options);
    }
}

/// <summary>
/// Design-time factory for the PostgreSQL migration set — used by
/// <c>dotnet ef migrations add --context PostgresDbContext
/// --output-dir Persistence/Migrations/Postgres</c>.
/// </summary>
public sealed class PostgresDesignTimeFactory
    : IDesignTimeDbContextFactory<PostgresDbContext>
{
    /// <inheritdoc />
    public PostgresDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<PostgresDbContext>();
        builder.UseNpgsql("Host=localhost;Database=openhandsnet_design")
            .UseSnakeCaseNamingConvention();
        return new PostgresDbContext(builder.Options);
    }
}
