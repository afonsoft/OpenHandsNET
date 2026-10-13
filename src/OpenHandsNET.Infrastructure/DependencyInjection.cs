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

        services.Configure<MultiTenancyOptions>(
            configuration.GetSection("MultiTenancy"));
        if (provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            // Migration set separada (Persistence/Migrations/Postgres).
            services.AddDbContext<PostgresDbContext>(options =>
                options.UseNpgsql(connectionString)
                    .UseSnakeCaseNamingConvention());
            services.AddScoped<AppDbContext>(sp =>
                sp.GetRequiredService<PostgresDbContext>());
        }
        else
        {
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite(connectionString)
                    .UseSnakeCaseNamingConvention()
                    .AddInterceptors(new SqlitePragmaInterceptor()));
        }
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

        // Git/SCM (SPEC-12): git CLI no container, credenciais via secrets+askpass.
        services.Configure<OpenHandsNET.Infrastructure.Git.GitOptions>(
            configuration.GetSection("Git"));
        services.AddScoped<OpenHandsNET.Infrastructure.Git.GitCredentialProvider>();
        services.AddScoped<OpenHandsNET.Application.Git.IGitService,
            OpenHandsNET.Infrastructure.Git.GitService>();

        // Conversations (SPEC-04): aggregate service + start-task queue/worker.
        services.AddSingleton<OpenHandsNET.Infrastructure.Conversations.StartTaskService>();
        services.AddSingleton<OpenHandsNET.Application.Conversations.IStartTaskService>(
            sp => sp.GetRequiredService<
                OpenHandsNET.Infrastructure.Conversations.StartTaskService>());
        services.AddScoped<OpenHandsNET.Application.Conversations.IAppConversationService,
            OpenHandsNET.Infrastructure.Conversations.AppConversationService>();
        services.AddHostedService<
            OpenHandsNET.Infrastructure.Conversations.ConversationStartWorker>();

        // Agent engine (SPEC-09): LLM provider + tool registry + runner host.
        services.Configure<OpenHandsNET.Application.Agent.AgentLlmOptions>(
            configuration.GetSection("AgentLlm"));
        services.PostConfigure<OpenHandsNET.Application.Agent.AgentLlmOptions>(o =>
        {
            // Chaves flat legadas (estilo upstream): LLM__MODEL/BASE_URL/API_KEY.
            o.Model = configuration["LLM:MODEL"] ?? o.Model;
            o.BaseUrl ??= configuration["LLM:BASE_URL"];
            o.ApiKey ??= configuration["LLM:API_KEY"];
        });
        services.AddHttpClient("agent-llm");
        services.AddSingleton<
            OpenHandsNET.Application.Agent.ITaskTrackerService,
            OpenHandsNET.Infrastructure.Agent.InMemoryTaskTrackerService>();
        services.AddSingleton<
            OpenHandsNET.Application.Agent.IToolRegistry,
            OpenHandsNET.Infrastructure.Agent.ToolRegistry>();
        services.AddSingleton<
            OpenHandsNET.Application.Agent.ICondenser,
            OpenHandsNET.Infrastructure.Agent.NoOpCondenser>();
        services.AddSingleton<OpenHandsNET.Infrastructure.Agent.AgentRunnerHost>();
        services.AddSingleton<OpenHandsNET.Application.Agent.IAgentRunnerHost>(
            sp => sp.GetRequiredService<
                OpenHandsNET.Infrastructure.Agent.AgentRunnerHost>());
        services.AddSingleton<OpenHandsNET.Application.Agent.IAgentLlmProvider>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options
                .IOptions<OpenHandsNET.Application.Agent.AgentLlmOptions>>();
            var o = opts.Value;
            var deploymentMode = configuration["DeploymentMode"] ?? "oss";
            var requested = (o.Provider ?? "").Trim();
            var autoMock = requested.Length == 0
                && string.IsNullOrEmpty(o.ApiKey)
                && deploymentMode.Equals("oss", StringComparison.OrdinalIgnoreCase);
            var useMock = requested.Equals("mock", StringComparison.OrdinalIgnoreCase)
                || autoMock;
            if (useMock)
            {
                // Guardrail: mock nunca em cloud/self_hosted sem AllowMock.
                if (!deploymentMode.Equals("oss", StringComparison.OrdinalIgnoreCase)
                    && !o.AllowMock)
                {
                    throw new InvalidOperationException(
                        "MockLlmProvider is dev/test only (deployment_mode != oss " +
                        "requires AgentLlm:AllowMock=true).");
                }

                return new OpenHandsNET.Infrastructure.Agent.MockLlmProvider(opts);
            }

            return Microsoft.Extensions.DependencyInjection.ActivatorUtilities
                .CreateInstance<
                    OpenHandsNET.Infrastructure.Agent.OpenAiCompatLlmProvider>(sp);
        });
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
