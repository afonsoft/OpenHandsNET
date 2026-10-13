using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Domain.Configuration;
using OpenHandsNET.Infrastructure.Eventing;

namespace OpenHandsNET.Infrastructure.Persistence;

/// <summary>
/// Application database context (SQLite by default, PostgreSQL for enterprise).
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>Key-value application settings.</summary>
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    /// <summary>Append-only conversation event stream.</summary>
    public DbSet<EventRecord> Events => Set<EventRecord>();

    /// <summary>Sandbox containers bound to conversations.</summary>
    public DbSet<OpenHandsNET.Infrastructure.Sandbox.SandboxRecord> Sandboxes =>
        Set<OpenHandsNET.Infrastructure.Sandbox.SandboxRecord>();

    /// <summary>Sandbox spec templates.</summary>
    public DbSet<OpenHandsNET.Infrastructure.Sandbox.SandboxSpecRecord> SandboxSpecs =>
        Set<OpenHandsNET.Infrastructure.Sandbox.SandboxSpecRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
