using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Infrastructure.Cache;
using OpenHandsNET.Infrastructure.Eventing;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Sandboxing;

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
        services.AddSingleton<InMemoryEventBus>();
        services.AddSingleton<IEventBus>(sp =>
            // SPEC-36 RF-002: fan-out cross-réplica só quando Redis —
            // single-réplica segue in-process (sem overhead de pub/sub).
            CacheRegistration.RedisEnabled(configuration)
                ? new DistributedEventBus(
                    sp.GetRequiredService<InMemoryEventBus>(),
                    sp.GetRequiredService<
                        OpenHandsNET.Application.Coordination.ICoordinationBus>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging
                        .ILogger<DistributedEventBus>>())
                : sp.GetRequiredService<InMemoryEventBus>());
        services.AddScoped<IEventService, EventStore>();
        services.AddSingleton<WebhookDispatcher>();
        services.AddHostedService<EventDispatcher>();
        services.AddHttpClient("webhooks");

        // SPEC-36: coordination cache/bus — memory (default) ou redis
        // (exigido quando Deployment:Replicas > 1).
        services.AddCoordinationCache(configuration);

        // Sandbox (SPEC-03/35): engine layer (docker local | runtime-api
        // remote) via ISandboxEngine; records/specs ficam no Api.
        services.AddSandboxing(configuration);
        services.AddSingleton<OpenHandsNET.Sandboxing.WarmPoolService>();
        services.AddHostedService(p => p.GetRequiredService<
            OpenHandsNET.Sandboxing.WarmPoolService>());
        services.AddScoped<OpenHandsNET.Application.Sandbox.ISandboxService,
            OpenHandsNET.Infrastructure.Sandbox.SandboxService>();
        // SPEC-17: executor dual-mode — HTTP agent in-container quando a
        // imagem traz o sandbox-agent; docker exec como fallback legado.
        services.AddHttpClient("sandbox-agent");
        // SPEC-38: sandbox host/path router upstream calls.
        services.AddHttpClient("sandbox-router");
        services.AddSingleton<
            OpenHandsNET.Infrastructure.Sandbox.SandboxAgentClient>();
        services.AddScoped<OpenHandsNET.Application.Sandbox.ISandboxExecutor,
            OpenHandsNET.Infrastructure.Sandbox.SandboxAgentExecutor>();
        services.AddScoped<
            OpenHandsNET.Infrastructure.Sandbox.BlueprintService>();
        services.AddHostedService<OpenHandsNET.Infrastructure.Sandbox.OrphanSweepService>();
        services.AddHostedService<OpenHandsNET.Infrastructure.Sandbox.SandboxTimeoutWatcher>();
        services.AddSingleton<OpenHandsNET.Infrastructure.Sandbox.SandboxSleepService>();

        // Git/SCM (SPEC-12): git CLI no container, credenciais via secrets+askpass.
        services.Configure<OpenHandsNET.Infrastructure.Git.GitOptions>(
            configuration.GetSection("Git"));
        services.AddScoped<OpenHandsNET.Infrastructure.Git.GitCredentialProvider>();
        services.AddScoped<OpenHandsNET.Application.Git.IGitService,
            OpenHandsNET.Infrastructure.Git.GitService>();
        // SPEC-52: SCM (GitHub App + webhooks + PR).
        services.AddHttpClient("scm-github-app");
        services.AddScoped<
            OpenHandsNET.Application.Scm.IScmProviderRegistry,
            OpenHandsNET.Infrastructure.Scm.ScmProviderRegistry>();
        services.AddScoped<
            OpenHandsNET.Application.Scm.IScmConnectionService,
            OpenHandsNET.Infrastructure.Scm.ScmConnectionService>();
        services.AddScoped<
            OpenHandsNET.Application.Scm.IScmWebhookService,
            OpenHandsNET.Infrastructure.Scm.GitHubWebhookService>();
        services.AddScoped<
            OpenHandsNET.Application.Scm.IScmPullRequestService,
            OpenHandsNET.Infrastructure.Scm.ScmPullRequestService>();
        services.AddScoped<
            OpenHandsNET.Application.Scm.IScmStatusReporter,
            OpenHandsNET.Infrastructure.Scm.ScmStatusReporter>();
        services.AddScoped<
            OpenHandsNET.Infrastructure.Scm.IGitHubClientFactory,
            OpenHandsNET.Infrastructure.Scm.GitHubAppClientFactory>();
        services.AddScoped<
            OpenHandsNET.Application.Scm.IScmProvider>(sp =>
        {
            var f = sp.GetRequiredService<OpenHandsNET.Infrastructure.Scm
                .IGitHubClientFactory>();
            var log = sp.GetRequiredService<Microsoft.Extensions.Logging
                .ILogger<OpenHandsNET.Infrastructure.Scm.GitHubScmProvider>>();
            return new OpenHandsNET.Infrastructure.Scm.GitHubScmProvider(
                f, log);
        });

        // SPEC-53: providers REST (GitLab/Bitbucket/Azure DevOps) — mesmo
        // IScmProvider, registrados por kind.
        services.AddScoped<
            OpenHandsNET.Application.Scm.IScmProvider>(sp =>
        {
            var hf = sp.GetRequiredService<IHttpClientFactory>();
            var sc = sp.GetRequiredService<
                OpenHandsNET.Application.Contracts.ISecretStore>();
            var log = sp.GetRequiredService<Microsoft.Extensions.Logging
                .ILogger<OpenHandsNET.Infrastructure.Scm.GitLabScmProvider>>();
            return new OpenHandsNET.Infrastructure.Scm.GitLabScmProvider(
                hf, sc, log);
        });
        services.AddScoped<
            OpenHandsNET.Application.Scm.IScmProvider>(sp =>
        {
            var hf = sp.GetRequiredService<IHttpClientFactory>();
            var sc = sp.GetRequiredService<
                OpenHandsNET.Application.Contracts.ISecretStore>();
            var log = sp.GetRequiredService<Microsoft.Extensions.Logging
                .ILogger<OpenHandsNET.Infrastructure.Scm
                    .BitbucketScmProvider>>();
            return new OpenHandsNET.Infrastructure.Scm.BitbucketScmProvider(
                hf, sc, log);
        });
        services.AddScoped<
            OpenHandsNET.Application.Scm.IScmProvider>(sp =>
        {
            var hf = sp.GetRequiredService<IHttpClientFactory>();
            var sc = sp.GetRequiredService<
                OpenHandsNET.Application.Contracts.ISecretStore>();
            var log = sp.GetRequiredService<Microsoft.Extensions.Logging
                .ILogger<OpenHandsNET.Infrastructure.Scm
                    .AzureDevOpsScmProvider>>();
            return new OpenHandsNET.Infrastructure.Scm
                .AzureDevOpsScmProvider(hf, sc, log);
        });

        // SPEC-54 ChatOps: Slack connections, sink, pipeline e reporter.
        services.AddScoped<
            OpenHandsNET.Application.ChatOps.IChatConnectionService,
            OpenHandsNET.Infrastructure.ChatOps.ChatConnectionService>();
        services.AddScoped<
            OpenHandsNET.Application.ChatOps.IChatOpsService,
            OpenHandsNET.Infrastructure.ChatOps.SlackChatOpsService>();
        services.AddScoped<
            OpenHandsNET.Application.ChatOps.IChatSink,
            OpenHandsNET.Infrastructure.ChatOps.SlackChatSink>();
        services.AddScoped<
            OpenHandsNET.Application.ChatOps.IChatOpsReporter,
            OpenHandsNET.Infrastructure.ChatOps.ChatOpsReporter>();

        // SPEC-55 Tickets: Linear/Jira connections, providers,
        // trigger pipeline e write-back.
        services.AddScoped<
            OpenHandsNET.Application.Tickets.ITicketConnectionService,
            OpenHandsNET.Infrastructure.Tickets.TicketConnectionService>();
        services.AddScoped<
            OpenHandsNET.Application.Tickets.ITicketTriggerService,
            OpenHandsNET.Infrastructure.Tickets.TicketTriggerService>();
        services.AddScoped<
            OpenHandsNET.Application.Tickets.ITicketReporter,
            OpenHandsNET.Infrastructure.Tickets.TicketReporter>();
        services.AddScoped<
            IReadOnlyDictionary<
                OpenHandsNET.Application.Tickets.TicketKind,
                OpenHandsNET.Application.Tickets.ITicketProvider>>(sp =>
            new Dictionary<
                OpenHandsNET.Application.Tickets.TicketKind,
                OpenHandsNET.Application.Tickets.ITicketProvider>
            {
                [OpenHandsNET.Application.Tickets.TicketKind.Linear] =
                    new OpenHandsNET.Infrastructure.Tickets
                        .LinearTicketProvider(
                            sp.GetRequiredService<IHttpClientFactory>(),
                            sp.GetRequiredService<
                                OpenHandsNET.Application.Contracts
                                    .ISecretStore>(),
                            sp.GetRequiredService<ILogger<
                                OpenHandsNET.Infrastructure.Tickets
                                    .LinearTicketProvider>>()),
                [OpenHandsNET.Application.Tickets.TicketKind.Jira] =
                    new OpenHandsNET.Infrastructure.Tickets
                        .JiraTicketProvider(
                            sp.GetRequiredService<IHttpClientFactory>(),
                            sp.GetRequiredService<
                                OpenHandsNET.Application.Contracts
                                    .ISecretStore>(),
                            sp.GetRequiredService<ILogger<
                                OpenHandsNET.Infrastructure.Tickets
                                    .JiraTicketProvider>>()),
            });

        // Checkpoints (SPEC-43): git-dir separado no sandbox, snapshots
        // pós-turno/manuais, revert com drift guard.
        services.Configure<OpenHandsNET.Application.Checkpoints.CheckpointOptions>(
            configuration.GetSection("Checkpoints"));
        services.AddScoped<OpenHandsNET.Application.Checkpoints.ICheckpointService,
            OpenHandsNET.Infrastructure.Checkpoints.CheckpointService>();

        // SPEC-44: invalidação do inbox de attention — tick sem payload,
        // client refaz fetch com scoping normal.
        services.AddSingleton<
            OpenHandsNET.Application.Conversations.IAttentionBus,
            OpenHandsNET.Infrastructure.Conversations.AttentionBus>();

        // Conversations (SPEC-04): aggregate service + start-task queue/worker.
        services.AddSingleton<OpenHandsNET.Infrastructure.Conversations.StartTaskService>();
        services.AddSingleton<OpenHandsNET.Application.Conversations.IStartTaskService>(
            sp => sp.GetRequiredService<
                OpenHandsNET.Infrastructure.Conversations.StartTaskService>());
        services.AddScoped<OpenHandsNET.Application.Conversations.IAppConversationService,
            OpenHandsNET.Infrastructure.Conversations.AppConversationService>();
        services.AddHostedService<
            OpenHandsNET.Infrastructure.Conversations.ConversationStartWorker>();

        // SPEC-47: automations — scheduler de usuário (start-task de
        // conversa por agenda ou webhook trigger anônimo).
        services.AddScoped<OpenHandsNET.Application.Automations.IAutomationService,
            OpenHandsNET.Infrastructure.Automations.AutomationService>();
        services.AddHostedService<
            OpenHandsNET.Infrastructure.Automations.AutomationScheduler>();

        // SPEC-56: public share links + trajectory export — filtro
        // público é camada separada (regressão de leak = teste falho).
        services.AddSingleton<
            OpenHandsNET.Application.Sharing.IPublicEventFilter,
            OpenHandsNET.Infrastructure.Sharing.PublicEventFilter>();
        services.AddScoped<OpenHandsNET.Application.Sharing.IShareService,
            OpenHandsNET.Infrastructure.Sharing.ShareService>();
        services.AddScoped<OpenHandsNET.Application.Sharing.ITrajectoryExporter,
            OpenHandsNET.Infrastructure.Sharing.TrajectoryExporter>();

        // Skills & hooks (SPEC-11): skill store + builtin seeder + hook runner.
        services.AddScoped<OpenHandsNET.Application.Skills.ISkillService,
            OpenHandsNET.Infrastructure.Skills.SkillService>();
        // SPEC-25: repo discovery + repo skills federadas + output policy
        // + web_fetch (egress do worker, SSRF guard).
        services.AddSingleton<
            OpenHandsNET.Application.Discovery.IRepoDiscoveryService,
            OpenHandsNET.Infrastructure.Discovery.RepoDiscoveryService>();
        services.AddSingleton<
            OpenHandsNET.Infrastructure.Skills.RepoSkillProvider>();
        services.AddScoped<
            OpenHandsNET.Infrastructure.Discovery.RepoContextLoader>();
        services.AddSingleton<
            OpenHandsNET.Infrastructure.Agent.Tools.OutputPolicy>();
        services.AddScoped<OpenHandsNET.Application.Web.IWebFetchService,
            OpenHandsNET.Infrastructure.Web.WebFetchService>();
        services.AddHttpClient("web-fetch", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(
                "OpenHandsNET/1.0 web_fetch");
        });
        services.AddScoped<OpenHandsNET.Application.Hooks.IHookService,
            OpenHandsNET.Infrastructure.Hooks.HookService>();
        // SPEC-30 RF-001: memória org-scoped (DB + audit trail).
        services.AddScoped<OpenHandsNET.Application.Memory.IMemoryService,
            OpenHandsNET.Infrastructure.Memory.MemoryService>();
        services.AddHostedService<
            OpenHandsNET.Infrastructure.Skills.BuiltinSkillSeedService>();

        // Identity & RBAC (SPEC-08): orgs, keys, device flow, admin, audit.
        services.Configure<OpenHandsNET.Infrastructure.Identity.AuthOptions>(
            configuration.GetSection(
                OpenHandsNET.Infrastructure.Identity.AuthOptions.SectionName));
        services.AddScoped<OpenHandsNET.Application.Identity.IEffectiveOrgResolver,
            OpenHandsNET.Infrastructure.Identity.EffectiveOrgResolver>();
        services.AddScoped<OpenHandsNET.Application.Identity.IUserService,
            OpenHandsNET.Infrastructure.Identity.UserService>();
        services.AddScoped<OpenHandsNET.Application.Identity.IApiKeyService,
            OpenHandsNET.Infrastructure.Identity.ApiKeyService>();
        services.AddScoped<OpenHandsNET.Application.Identity.IServiceUserService,
            OpenHandsNET.Infrastructure.Identity.ServiceUserService>();
        services.AddScoped<OpenHandsNET.Application.Identity.IOrgService,
            OpenHandsNET.Infrastructure.Identity.OrgService>();
        services.AddScoped<OpenHandsNET.Application.Identity.IInvitationService,
            OpenHandsNET.Infrastructure.Identity.InvitationService>();
        services.AddScoped<OpenHandsNET.Application.Identity.ISuperAdminService,
            OpenHandsNET.Infrastructure.Identity.SuperAdminService>();
        services.AddScoped<OpenHandsNET.Application.Identity.IAdminUserService,
            OpenHandsNET.Infrastructure.Identity.AdminUserService>();
        services.AddScoped<OpenHandsNET.Application.Identity.IProvisioningService,
            OpenHandsNET.Infrastructure.Identity.ProvisioningService>();
        services.AddScoped<OpenHandsNET.Application.Identity.IDeviceFlowService,
            OpenHandsNET.Infrastructure.Identity.DeviceFlowService>();
        services.AddScoped<OpenHandsNET.Application.Identity.IQuotaService,
            OpenHandsNET.Infrastructure.Identity.QuotaService>();
        services.AddScoped<OpenHandsNET.Application.Identity.IAuditService,
            OpenHandsNET.Infrastructure.Identity.AuditService>();
        services.AddScoped<OpenHandsNET.Application.Identity.IFeatureFlagService,
            OpenHandsNET.Infrastructure.Identity.FeatureFlagService>();
        services.AddScoped<OpenHandsNET.Application.Identity.IUsageService,
            OpenHandsNET.Infrastructure.Identity.UsageService>();

        // SPEC-20: billing — enforcement, metering write-path e catálogo.
        services.Configure<OpenHandsNET.Infrastructure.Identity.BillingOptions>(
            configuration.GetSection(
                OpenHandsNET.Infrastructure.Identity.BillingOptions.Section));
        services.AddScoped<OpenHandsNET.Application.Identity.IPlanEnforcementService,
            OpenHandsNET.Infrastructure.Identity.PlanEnforcementService>();
        // SPEC-36 RF-002: write-path bufferado (Redis) + flush worker
        // quando multi-réplica; single-réplica grava direto no EF.
        if (CacheRegistration.RedisEnabled(configuration))
        {
            services.AddScoped<OpenHandsNET.Application.Identity.IUsageMeter,
                OpenHandsNET.Infrastructure.Identity.BufferedUsageMeter>();
            services.AddHostedService<
                OpenHandsNET.Infrastructure.Identity.UsageFlushWorker>();
        }
        else
        {
            services.AddScoped<OpenHandsNET.Application.Identity.IUsageMeter,
                OpenHandsNET.Infrastructure.Identity.UsageMeter>();
        }
        services.AddScoped<OpenHandsNET.Application.Identity.IPlanAdminService,
            OpenHandsNET.Infrastructure.Identity.PlanAdminService>();
        services.AddScoped<
            OpenHandsNET.Infrastructure.Observability.SupportBundleService>();
        services.AddSingleton<OpenHandsNET.Application.Identity.IAnalyticsService,
            OpenHandsNET.Infrastructure.Identity.NullAnalyticsService>();
        services.AddSingleton<
            OpenHandsNET.Infrastructure.Identity.Provisioning.IBrokerUserAdmin,
            OpenHandsNET.Infrastructure.Identity.Provisioning.NullBrokerUserAdmin>();

        // MCP (SPEC-07): settings CRUD + client singleton + OAuth + probe.
        services.Configure<OpenHandsNET.Infrastructure.Mcp.McpOptions>(
            configuration.GetSection(
                OpenHandsNET.Infrastructure.Mcp.McpOptions.SectionName));
        services.AddScoped<OpenHandsNET.Application.Mcp.IMcpSettingsService,
            OpenHandsNET.Infrastructure.Mcp.McpSettingsService>();
        services.AddScoped<OpenHandsNET.Application.Mcp.IMcpOAuthService,
            OpenHandsNET.Infrastructure.Mcp.McpOAuthService>();
        services.AddHttpClient("mcp-oauth");
        services.AddSingleton<
            OpenHandsNET.Infrastructure.Mcp.McpClientService>();
        services.AddSingleton<OpenHandsNET.Application.Mcp.IMcpClientService>(
            sp => sp.GetRequiredService<
                OpenHandsNET.Infrastructure.Mcp.McpClientService>());

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
        // SPEC-23: catálogo OmniRoute + router por role (singletons —
        // cache compartilhado; seleção é stateless por chamada).
        services.AddMemoryCache();
        services.AddSingleton<
            OpenHandsNET.Application.Agent.IModelCatalog,
            OpenHandsNET.Infrastructure.Agent.Routing.OmniRouteModelCatalog>();
        services.AddSingleton<
            OpenHandsNET.Application.Agent.IModelRouter,
            OpenHandsNET.Infrastructure.Agent.Routing.ModelRouter>();
        // SPEC-22: context engine — meter/condenser/builder scoped por run.
        services.AddScoped<
            OpenHandsNET.Application.Agent.IContextMeter,
            OpenHandsNET.Infrastructure.Agent.Context.HeuristicContextMeter>();
        services.AddScoped<
            OpenHandsNET.Application.Agent.ICondenser,
            OpenHandsNET.Infrastructure.Agent.Context.LlmSummarizingCondenser>();
        services.AddScoped<
            OpenHandsNET.Infrastructure.Agent.Context.ConversationHistoryBuilder>();
        services.AddScoped<
            OpenHandsNET.Application.Agent.ICompactionService,
            OpenHandsNET.Infrastructure.Agent.Context.CompactionService>();
        services.AddSingleton<OpenHandsNET.Infrastructure.Agent.AgentRunnerHost>();
        services.AddSingleton<OpenHandsNET.Application.Agent.IAgentRunnerHost>(
            sp => sp.GetRequiredService<
                OpenHandsNET.Infrastructure.Agent.AgentRunnerHost>());
        // SPEC-33 RF-001: security analyzer — pattern (default, local) ou
        // llm (opt-in, classifica via completion + LRU cache).
        services.AddSingleton<
            OpenHandsNET.Application.Agent.ISecurityAnalyzer>(sp =>
        {
            var o = sp.GetRequiredService<Microsoft.Extensions.Options
                .IOptions<OpenHandsNET.Application.Agent.AgentLlmOptions>>()
                .Value;
            return o.SecurityAnalyzer.Equals("llm",
                StringComparison.OrdinalIgnoreCase)
                ? new OpenHandsNET.Infrastructure.Agent.Security
                    .LlmSecurityAnalyzer(
                        sp.GetRequiredService<OpenHandsNET.Application.Agent
                            .IAgentLlmProvider>(),
                        sp.GetRequiredService<Microsoft.Extensions.Caching
                            .Memory.IMemoryCache>())
                    : (OpenHandsNET.Application.Agent.ISecurityAnalyzer)
                        new OpenHandsNET.Infrastructure.Agent.Security
                            .PatternSecurityAnalyzer();
        });
        // SPEC-48 RF-001: política de segurança por org — presets +
        // overrides, cache distribuído (TTL 60s) com invalidação no write.
        services.AddScoped<
            OpenHandsNET.Application.Agent.ISecurityPolicyService>(sp =>
            new OpenHandsNET.Infrastructure.Security.SecurityPolicyService(
                sp.GetRequiredService<OpenHandsNET.Infrastructure.Persistence
                    .AppDbContext>(),
                sp.GetService<Microsoft.Extensions.Caching.Distributed
                    .IDistributedCache>()));
        // SPEC-50 RF-001/002: BYOR LiteLLM — admin client tipado +
        // lifecycle de virtual keys (ciphertext DataProtection, uma
        // ativa por usuário). Off por default (LlmProxy:Mode=off).
        services.Configure<OpenHandsNET.Application.Agent.LlmProxyOptions>(
            configuration.GetSection("LlmProxy"));
        services.AddHttpClient("litellm");
        services.AddSingleton<
            OpenHandsNET.Application.Agent.ILiteLlmAdminClient,
            OpenHandsNET.Infrastructure.Agent.Routing.LiteLlmAdminClient>();
        services.AddScoped<
            OpenHandsNET.Application.Agent.IByorKeyService>(sp =>
            new OpenHandsNET.Infrastructure.Agent.Routing.ByorKeyService(
                sp.GetRequiredService<OpenHandsNET.Infrastructure.Persistence
                    .AppDbContext>(),
                sp.GetRequiredService<OpenHandsNET.Application.Agent
                    .ILiteLlmAdminClient>(),
                sp.GetRequiredService<Microsoft.Extensions.Options
                    .IOptions<OpenHandsNET.Application.Agent
                        .LlmProxyOptions>>(),
                sp.GetRequiredService<Microsoft.AspNetCore.DataProtection
                    .IDataProtectionProvider>(),
                sp.GetService<Microsoft.Extensions.Caching.Distributed
                    .IDistributedCache>()));
        // SPEC-33 RF-003: secret store criptografado (DataProtection) —
        // valores resolvem só na borda do sandbox.
        services.AddScoped<OpenHandsNET.Application.Contracts.ISecretStore,
            OpenHandsNET.Infrastructure.Identity.SecretStore>();
        // SPEC-28: registry in-memory de runs de subagentes (singleton —
        // compartilhado entre tools do mesmo escopo de processo).
        services.AddSingleton<OpenHandsNET.Application.Agent.ISubagentHost,
            OpenHandsNET.Infrastructure.Agent.Subagents.SubagentHost>();
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

        // SPEC-18 RF-001: browser headless no worker — singleton compartilhado,
        // um browser context isolado por conversa.
        services.Configure<
            OpenHandsNET.Infrastructure.Agent.Browser.BrowserOptions>(
            configuration.GetSection(
                OpenHandsNET.Infrastructure.Agent.Browser.BrowserOptions.Section));
        services.AddSingleton<OpenHandsNET.Application.Agent.IBrowserService,
            OpenHandsNET.Infrastructure.Agent.Browser.PlaywrightBrowserService>();

        // SPEC-34 RF-001: bus de deltas efêmeros (runner→hub; nunca EF).
        // SPEC-36 RF-002: cross-réplica via bus de coordenação quando Redis.
        services.AddSingleton<
            OpenHandsNET.Infrastructure.Agent.Deltas.AgentActivityBus>();
        services.AddSingleton<
            OpenHandsNET.Application.Events.IAgentActivityBus>(sp =>
            CacheRegistration.RedisEnabled(configuration)
                ? new OpenHandsNET.Infrastructure.Agent.Deltas
                    .DistributedAgentActivityBus(
                        sp.GetRequiredService<OpenHandsNET.Infrastructure
                            .Agent.Deltas.AgentActivityBus>(),
                        sp.GetRequiredService<OpenHandsNET.Application
                            .Coordination.ICoordinationBus>(),
                        sp.GetRequiredService<Microsoft.Extensions.Logging
                            .ILogger<OpenHandsNET.Infrastructure.Agent.Deltas
                                .DistributedAgentActivityBus>>())
                : sp.GetRequiredService<OpenHandsNET.Infrastructure.Agent
                    .Deltas.AgentActivityBus>());
        // SPEC-34 RF-002: artifact store — workspace default, s3 opt-in.
        services.Configure<
            OpenHandsNET.Infrastructure.Artifacts.ArtifactOptions>(
            configuration.GetSection(
                OpenHandsNET.Infrastructure.Artifacts.ArtifactOptions.Section));
        services.AddSingleton<
            OpenHandsNET.Application.Artifacts.IArtifactStore>(sp =>
        {
            var o = sp.GetRequiredService<Microsoft.Extensions.Options
                .IOptions<OpenHandsNET.Infrastructure.Artifacts
                    .ArtifactOptions>>();
            return o.Value.Provider.Equals("s3",
                StringComparison.OrdinalIgnoreCase)
                ? new OpenHandsNET.Infrastructure.Artifacts.S3ArtifactStore(o,
                    sp.GetRequiredService<Microsoft.Extensions.Logging
                        .ILogger<OpenHandsNET.Infrastructure.Artifacts
                            .S3ArtifactStore>>())
                : (OpenHandsNET.Application.Artifacts.IArtifactStore)
                    new OpenHandsNET.Infrastructure.Artifacts
                        .WorkspaceArtifactStore(o,
                            sp.GetRequiredService<Microsoft.Extensions.Logging
                                .ILogger<OpenHandsNET.Infrastructure.Artifacts
                                    .WorkspaceArtifactStore>>());
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
