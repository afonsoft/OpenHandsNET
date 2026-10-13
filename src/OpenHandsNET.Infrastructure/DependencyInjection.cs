using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Infrastructure.Eventing;
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

        // Event stream (SPEC-02): store + in-process fan-out + dispatcher/webhooks.
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddScoped<IEventService, EventStore>();
        services.AddSingleton<WebhookDispatcher>();
        services.AddHostedService<EventDispatcher>();
        services.AddHttpClient("webhooks");

        // Sandbox (SPEC-03): Docker lifecycle + executor + workspace + watchers.
        services.Configure<OpenHandsNET.Infrastructure.Sandbox.SandboxOptions>(
            configuration.GetSection("Sandbox"));
        services.AddSingleton(_ =>
            OpenHandsNET.Infrastructure.Sandbox.DockerClientFactory.Create());
        services.AddSingleton<OpenHandsNET.Infrastructure.Sandbox.WorkspaceProvisioner>();
        services.AddScoped<OpenHandsNET.Application.Sandbox.ISandboxService,
            OpenHandsNET.Infrastructure.Sandbox.DockerSandboxService>();
        services.AddScoped<OpenHandsNET.Application.Sandbox.ISandboxExecutor,
            OpenHandsNET.Infrastructure.Sandbox.DockerSandboxExecutor>();
        services.AddHostedService<OpenHandsNET.Infrastructure.Sandbox.OrphanSweepService>();
        services.AddHostedService<OpenHandsNET.Infrastructure.Sandbox.SandboxTimeoutWatcher>();

        // Conversations (SPEC-04): aggregate service + start-task queue/worker.
        services.AddSingleton<OpenHandsNET.Infrastructure.Conversations.StartTaskService>();
        services.AddSingleton<OpenHandsNET.Application.Conversations.IStartTaskService>(
            sp => sp.GetRequiredService<
                OpenHandsNET.Infrastructure.Conversations.StartTaskService>());
        services.AddScoped<OpenHandsNET.Application.Conversations.IAppConversationService,
            OpenHandsNET.Infrastructure.Conversations.AppConversationService>();
        services.AddHostedService<
            OpenHandsNET.Infrastructure.Conversations.ConversationStartWorker>();
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
