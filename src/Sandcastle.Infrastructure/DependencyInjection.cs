using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Events;
using Sandcastle.Infrastructure.Cache;
using Sandcastle.Infrastructure.Eventing;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Sandboxing;

namespace Sandcastle.Infrastructure;

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
        RegisterPersistence(services, configuration);
        RegisterEventing(services, configuration);
        RegisterSandboxGit(services, configuration);
        RegisterScmProviders(services);
        RegisterChatOpsTickets(services, configuration);
        RegisterConversations(services, configuration);
        RegisterIdentity(services, configuration);
        RegisterMcp(services, configuration);
        RegisterAgent(services, configuration);
        return services;
    }

    private static void RegisterPersistence(
        IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        var connectionString = configuration["Database:ConnectionString"]
            ?? "Data Source=data/sandcastle.db";

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

    }

    private static void RegisterEventing(
        IServiceCollection services, IConfiguration configuration)
    {
        // Event stream (SPEC-02): store + in-process fan-out + dispatcher/webhooks.
        services.AddSingleton<InMemoryEventBus>();
        services.AddSingleton<IEventBus>(sp =>
            // SPEC-36 RF-002: fan-out cross-réplica só quando Redis —
            // single-réplica segue in-process (sem overhead de pub/sub).
            CacheRegistration.RedisEnabled(configuration)
                ? new DistributedEventBus(
                    sp.GetRequiredService<InMemoryEventBus>(),
                    sp.GetRequiredService<
                        Sandcastle.Application.Coordination.ICoordinationBus>(),
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

    }

    private static void RegisterSandboxGit(
        IServiceCollection services, IConfiguration configuration)
    {
        // Sandbox (SPEC-03/35): engine layer (docker local | runtime-api
        // remote) via ISandboxEngine; records/specs ficam no Api.
        services.AddSandboxing(configuration);
        services.AddSingleton<Sandcastle.Sandboxing.WarmPoolService>();
        services.AddHostedService(p => p.GetRequiredService<
            Sandcastle.Sandboxing.WarmPoolService>());
        services.AddScoped<Sandcastle.Application.Sandbox.ISandboxService,
            Sandcastle.Infrastructure.Sandbox.SandboxService>();
        // SPEC-17: executor dual-mode — HTTP agent in-container quando a
        // imagem traz o sandbox-agent; docker exec como fallback legado.
        services.AddHttpClient("sandbox-agent");
        // SPEC-38: sandbox host/path router upstream calls.
        services.AddHttpClient("sandbox-router");
        services.AddSingleton<
            Sandcastle.Infrastructure.Sandbox.SandboxAgentClient>();
        services.AddScoped<Sandcastle.Application.Sandbox.ISandboxExecutor,
            Sandcastle.Infrastructure.Sandbox.SandboxAgentExecutor>();
        services.AddScoped<
            Sandcastle.Infrastructure.Sandbox.BlueprintService>();
        services.AddHostedService<Sandcastle.Infrastructure.Sandbox.OrphanSweepService>();
        services.AddHostedService<Sandcastle.Infrastructure.Sandbox.SandboxTimeoutWatcher>();
        services.AddSingleton<Sandcastle.Infrastructure.Sandbox.SandboxSleepService>();

        // Git/SCM (SPEC-12): git CLI no container, credenciais via secrets+askpass.
        services.Configure<Sandcastle.Infrastructure.Git.GitOptions>(
            configuration.GetSection("Git"));
        // SPEC-146: multi-repo — MaxRepos hard-cap 4.
        services.Configure<
            Sandcastle.Application.Conversations.MultiRepoOptions>(
            configuration.GetSection("MultiRepo"));
        services.AddScoped<
            Sandcastle.Infrastructure.Scm.RepoCloneCredentialResolver>();
        services.AddScoped<Sandcastle.Infrastructure.Git.GitCredentialProvider>();
        // SPEC-147 RF-002: autoria de commit user→org→global.
        services.AddScoped<
            Sandcastle.Infrastructure.Git.GitAuthorResolver>();
        services.AddScoped<Sandcastle.Application.Git.IGitService,
            Sandcastle.Infrastructure.Git.GitService>();
        // SPEC-52: SCM (GitHub App + webhooks + PR).
        services.AddHttpClient("scm-github-app");
        services.AddScoped<
            Sandcastle.Application.Scm.IScmProviderRegistry,
            Sandcastle.Infrastructure.Scm.ScmProviderRegistry>();
        services.AddScoped<
            Sandcastle.Application.Scm.IScmConnectionService,
            Sandcastle.Infrastructure.Scm.ScmConnectionService>();
        services.AddScoped<
            Sandcastle.Application.Scm.IScmWebhookService,
            Sandcastle.Infrastructure.Scm.GitHubWebhookService>();
        services.AddScoped<
            Sandcastle.Application.Scm.IScmPullRequestService,
            Sandcastle.Infrastructure.Scm.ScmPullRequestService>();
        services.AddScoped<
            Sandcastle.Application.Scm.IScmStatusReporter,
            Sandcastle.Infrastructure.Scm.ScmStatusReporter>();
        services.AddScoped<
            Sandcastle.Infrastructure.Scm.IGitHubClientFactory,
            Sandcastle.Infrastructure.Scm.GitHubAppClientFactory>();
        services.AddScoped<
            Sandcastle.Application.Scm.IScmProvider>(sp =>
        {
            var f = sp.GetRequiredService<Sandcastle.Infrastructure.Scm
                .IGitHubClientFactory>();
            var log = sp.GetRequiredService<Microsoft.Extensions.Logging
                .ILogger<Sandcastle.Infrastructure.Scm.GitHubScmProvider>>();
            return new Sandcastle.Infrastructure.Scm.GitHubScmProvider(
                f, log);
        });

        // SPEC-140: auto-review de PR — options, fila in-process + worker,
        // trigger service e runner (job próprio, não-conversa).
        services.Configure<
            Sandcastle.Infrastructure.Scm.Review.ReviewOptions>(
            configuration.GetSection(
                Sandcastle.Infrastructure.Scm.Review
                    .ReviewOptions.Section));
        services.AddSingleton<
            Sandcastle.Infrastructure.Scm.Review.ReviewDispatcher>();
        services.AddSingleton<
            Sandcastle.Application.Scm.IReviewDispatcher>(sp =>
            sp.GetRequiredService<
                Sandcastle.Infrastructure.Scm.Review.ReviewDispatcher>());
        services.AddHostedService<
            Sandcastle.Infrastructure.Scm.Review.ReviewRunWorker>();
        services.AddScoped<
            Sandcastle.Application.Scm.IReviewService,
            Sandcastle.Infrastructure.Scm.Review.ReviewService>();
        services.AddScoped<
            Sandcastle.Application.Scm.IReviewRunner,
            Sandcastle.Infrastructure.Scm.Review.ReviewRunner>();

        // SPEC-141: CI autofix loop — options, fila in-process + worker
        // (runs paralelos — um run pode pollar por até RunTimeoutMinutes),
        // trigger/policy service e runner (logs → fix → push → poll).
        services.Configure<
            Sandcastle.Infrastructure.Scm.Autofix.AutofixOptions>(
            configuration.GetSection(
                Sandcastle.Infrastructure.Scm.Autofix
                    .AutofixOptions.Section));
        services.AddSingleton<
            Sandcastle.Infrastructure.Scm.Autofix.AutofixDispatcher>();
        services.AddSingleton<
            Sandcastle.Application.Scm.IAutofixDispatcher>(sp =>
            sp.GetRequiredService<
                Sandcastle.Infrastructure.Scm.Autofix.AutofixDispatcher>());
        services.AddHostedService<
            Sandcastle.Infrastructure.Scm.Autofix.AutofixRunWorker>();
        services.AddScoped<
            Sandcastle.Application.Scm.IAutofixService,
            Sandcastle.Infrastructure.Scm.Autofix.AutofixService>();
        services.AddScoped<
            Sandcastle.Application.Scm.IAutofixRunner,
            Sandcastle.Infrastructure.Scm.Autofix.AutofixRunner>();

        // SPEC-142: QA agent com evidência — options, fila in-process +
        // worker (um run por vez — sandbox descartável dedicado),
        // trigger/dedup/quota service, runner do pipeline de 4 fases e
        // seam do driver browser (Playwright dentro do sandbox).
        services.Configure<
            Sandcastle.Infrastructure.Scm.Qa.QaOptions>(
            configuration.GetSection(
                Sandcastle.Infrastructure.Scm.Qa
                    .QaOptions.Section));
        services.AddSingleton<
            Sandcastle.Infrastructure.Scm.Qa.QaDispatcher>();
        services.AddSingleton<
            Sandcastle.Application.Scm.IQaDispatcher>(sp =>
            sp.GetRequiredService<
                Sandcastle.Infrastructure.Scm.Qa.QaDispatcher>());
        services.AddHostedService<
            Sandcastle.Infrastructure.Scm.Qa.QaRunWorker>();
        services.AddScoped<
            Sandcastle.Application.Scm.IQaService,
            Sandcastle.Infrastructure.Scm.Qa.QaService>();
        services.AddScoped<
            Sandcastle.Application.Scm.IQaRunner,
            Sandcastle.Infrastructure.Scm.Qa.QaRunner>();
        services.AddScoped<
            Sandcastle.Application.Scm.IQaBrowserDriverFactory,
            Sandcastle.Infrastructure.Scm.Qa
                .SandboxQaBrowserDriverFactory>();
    }

    private static void RegisterScmProviders(
        IServiceCollection services)
    {
        // SPEC-53: providers REST (GitLab/Bitbucket/Azure DevOps) — mesmo
        // IScmProvider, registrados por kind.
        services.AddScoped<
            Sandcastle.Application.Scm.IScmProvider>(sp =>
        {
            var hf = sp.GetRequiredService<IHttpClientFactory>();
            var sc = sp.GetRequiredService<
                Sandcastle.Application.Contracts.ISecretStore>();
            var log = sp.GetRequiredService<Microsoft.Extensions.Logging
                .ILogger<Sandcastle.Infrastructure.Scm.GitLabScmProvider>>();
            return new Sandcastle.Infrastructure.Scm.GitLabScmProvider(
                hf, sc, log);
        });
        services.AddScoped<
            Sandcastle.Application.Scm.IScmProvider>(sp =>
        {
            var hf = sp.GetRequiredService<IHttpClientFactory>();
            var sc = sp.GetRequiredService<
                Sandcastle.Application.Contracts.ISecretStore>();
            var log = sp.GetRequiredService<Microsoft.Extensions.Logging
                .ILogger<Sandcastle.Infrastructure.Scm
                    .BitbucketScmProvider>>();
            return new Sandcastle.Infrastructure.Scm.BitbucketScmProvider(
                hf, sc, log);
        });
        services.AddScoped<
            Sandcastle.Application.Scm.IScmProvider>(sp =>
        {
            var hf = sp.GetRequiredService<IHttpClientFactory>();
            var sc = sp.GetRequiredService<
                Sandcastle.Application.Contracts.ISecretStore>();
            var log = sp.GetRequiredService<Microsoft.Extensions.Logging
                .ILogger<Sandcastle.Infrastructure.Scm
                    .AzureDevOpsScmProvider>>();
            return new Sandcastle.Infrastructure.Scm
                .AzureDevOpsScmProvider(hf, sc, log);
        });

    }

    private static void RegisterChatOpsTickets(
        IServiceCollection services, IConfiguration configuration)
    {
        // SPEC-54 ChatOps: Slack connections, sink, pipeline e reporter.
        // SPEC-147 RF-001: ChatOps:AllowGroupMentions (default false).
        services.Configure<
            Sandcastle.Infrastructure.ChatOps.ChatOpsOptions>(
            configuration.GetSection(
                Sandcastle.Infrastructure.ChatOps.ChatOpsOptions.Section));
        services.AddScoped<
            Sandcastle.Application.ChatOps.IChatConnectionService,
            Sandcastle.Infrastructure.ChatOps.ChatConnectionService>();
        services.AddScoped<
            Sandcastle.Application.ChatOps.IChatOpsService,
            Sandcastle.Infrastructure.ChatOps.SlackChatOpsService>();
        services.AddScoped<
            Sandcastle.Application.ChatOps.IChatSink,
            Sandcastle.Infrastructure.ChatOps.SlackChatSink>();
        services.AddScoped<
            Sandcastle.Application.ChatOps.IChatOpsReporter,
            Sandcastle.Infrastructure.ChatOps.ChatOpsReporter>();

        // SPEC-55 Tickets: Linear/Jira connections, providers,
        // trigger pipeline e write-back.
        services.AddScoped<
            Sandcastle.Application.Tickets.ITicketConnectionService,
            Sandcastle.Infrastructure.Tickets.TicketConnectionService>();
        services.AddScoped<
            Sandcastle.Application.Tickets.ITicketTriggerService,
            Sandcastle.Infrastructure.Tickets.TicketTriggerService>();
        services.AddScoped<
            Sandcastle.Application.Tickets.ITicketReporter,
            Sandcastle.Infrastructure.Tickets.TicketReporter>();
        services.AddScoped<
            IReadOnlyDictionary<
                Sandcastle.Application.Tickets.TicketKind,
                Sandcastle.Application.Tickets.ITicketProvider>>(sp =>
            new Dictionary<
                Sandcastle.Application.Tickets.TicketKind,
                Sandcastle.Application.Tickets.ITicketProvider>
            {
                [Sandcastle.Application.Tickets.TicketKind.Linear] =
                    new Sandcastle.Infrastructure.Tickets
                        .LinearTicketProvider(
                            sp.GetRequiredService<IHttpClientFactory>(),
                            sp.GetRequiredService<
                                Sandcastle.Application.Contracts
                                    .ISecretStore>(),
                            sp.GetRequiredService<ILogger<
                                Sandcastle.Infrastructure.Tickets
                                    .LinearTicketProvider>>()),
                [Sandcastle.Application.Tickets.TicketKind.Jira] =
                    new Sandcastle.Infrastructure.Tickets
                        .JiraTicketProvider(
                            sp.GetRequiredService<IHttpClientFactory>(),
                            sp.GetRequiredService<
                                Sandcastle.Application.Contracts
                                    .ISecretStore>(),
                            sp.GetRequiredService<ILogger<
                                Sandcastle.Infrastructure.Tickets
                                    .JiraTicketProvider>>()),
            });

    }

    private static void RegisterConversations(
        IServiceCollection services, IConfiguration configuration)
    {
        // Checkpoints (SPEC-43): git-dir separado no sandbox, snapshots
        // pós-turno/manuais, revert com drift guard.
        services.Configure<Sandcastle.Application.Checkpoints.CheckpointOptions>(
            configuration.GetSection("Checkpoints"));
        services.AddScoped<Sandcastle.Application.Checkpoints.ICheckpointService,
            Sandcastle.Infrastructure.Checkpoints.CheckpointService>();

        // SPEC-44: invalidação do inbox de attention — tick sem payload,
        // client refaz fetch com scoping normal.
        services.AddSingleton<
            Sandcastle.Application.Conversations.IAttentionBus,
            Sandcastle.Infrastructure.Conversations.AttentionBus>();

        // Conversations (SPEC-04): aggregate service + start-task queue/worker.
        services.AddSingleton<Sandcastle.Infrastructure.Conversations.StartTaskService>();
        services.AddSingleton<Sandcastle.Application.Conversations.IStartTaskService>(
            sp => sp.GetRequiredService<
                Sandcastle.Infrastructure.Conversations.StartTaskService>());
        services.AddScoped<Sandcastle.Application.Conversations.IAppConversationService,
            Sandcastle.Infrastructure.Conversations.AppConversationService>();
        // SPEC-135: fork + turn undo + hunk revert.
        services.AddScoped<Sandcastle.Application.Conversations.IConversationUxService,
            Sandcastle.Infrastructure.Conversations.ConversationUxService>();
        services.AddHostedService<
            Sandcastle.Infrastructure.Conversations.ConversationStartWorker>();

        // SPEC-47: automations — scheduler de usuário (start-task de
        // conversa por agenda ou webhook trigger anônimo).
        services.AddScoped<Sandcastle.Application.Automations.IAutomationService,
            Sandcastle.Infrastructure.Automations.AutomationService>();
        services.AddHostedService<
            Sandcastle.Infrastructure.Automations.AutomationScheduler>();

        // SPEC-56: public share links + trajectory export — filtro
        // público é camada separada (regressão de leak = teste falho).
        services.AddSingleton<
            Sandcastle.Application.Sharing.IPublicEventFilter,
            Sandcastle.Infrastructure.Sharing.PublicEventFilter>();
        services.AddScoped<Sandcastle.Application.Sharing.IShareService,
            Sandcastle.Infrastructure.Sharing.ShareService>();
        services.AddScoped<Sandcastle.Application.Sharing.ITrajectoryExporter,
            Sandcastle.Infrastructure.Sharing.TrajectoryExporter>();

        // Skills & hooks (SPEC-11): skill store + builtin seeder + hook runner.
        services.AddScoped<Sandcastle.Application.Skills.ISkillService,
            Sandcastle.Infrastructure.Skills.SkillService>();
        // SPEC-25: repo discovery + repo skills federadas + output policy
        // + web_fetch (egress do worker, SSRF guard).
        services.AddSingleton<
            Sandcastle.Application.Discovery.IRepoDiscoveryService,
            Sandcastle.Infrastructure.Discovery.RepoDiscoveryService>();
        services.AddSingleton<
            Sandcastle.Infrastructure.Skills.RepoSkillProvider>();
        services.AddScoped<
            Sandcastle.Infrastructure.Discovery.RepoContextLoader>();
        services.AddSingleton<
            Sandcastle.Infrastructure.Agent.Tools.OutputPolicy>();
        services.AddScoped<Sandcastle.Application.Web.IWebFetchService,
            Sandcastle.Infrastructure.Web.WebFetchService>();
        services.AddHttpClient("web-fetch", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Sandcastle/1.0 web_fetch");
        });
        // SPEC-133 RF-001/RF-002: web_search provider-backed —
        // duckduckgo default sem key; tavily/brave via WebSearch:ApiKey.
        services.Configure<Sandcastle.Application.Web.WebSearchOptions>(
            configuration.GetSection(
                Sandcastle.Application.Web.WebSearchOptions.Section));
        services.AddScoped<Sandcastle.Application.Web.IWebSearchService,
            Sandcastle.Infrastructure.Web.WebSearchService>();
        services.AddHttpClient("web-search", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Sandcastle/1.0 web_search");
        });
        services.AddScoped<Sandcastle.Application.Hooks.IHookService,
            Sandcastle.Infrastructure.Hooks.HookService>();
        // SPEC-127: hooks de arquivo (.openhands/hooks/*.hook.json) —
        // scoped como IHookService: consome IEventService (scoped).
        services.AddScoped<
            Sandcastle.Application.Hooks.IWorkspaceHookService,
            Sandcastle.Infrastructure.Hooks.WorkspaceHookService>();
        // SPEC-30 RF-001: memória org-scoped (DB + audit trail).
        services.AddScoped<Sandcastle.Application.Memory.IMemoryService,
            Sandcastle.Infrastructure.Memory.MemoryService>();
        services.AddHostedService<
            Sandcastle.Infrastructure.Skills.BuiltinSkillSeedService>();

    }

    private static void RegisterIdentity(
        IServiceCollection services, IConfiguration configuration)
    {
        // Identity & RBAC (SPEC-08): orgs, keys, device flow, admin, audit.
        services.Configure<Sandcastle.Infrastructure.Identity.AuthOptions>(
            configuration.GetSection(
                Sandcastle.Infrastructure.Identity.AuthOptions.SectionName));
        services.AddScoped<Sandcastle.Application.Identity.IEffectiveOrgResolver,
            Sandcastle.Infrastructure.Identity.EffectiveOrgResolver>();
        services.AddScoped<Sandcastle.Application.Identity.IUserService,
            Sandcastle.Infrastructure.Identity.UserService>();
        services.AddScoped<Sandcastle.Application.Identity.IApiKeyService,
            Sandcastle.Infrastructure.Identity.ApiKeyService>();
        services.AddScoped<Sandcastle.Application.Identity.IServiceUserService,
            Sandcastle.Infrastructure.Identity.ServiceUserService>();
        // SPEC-63: knowledge pipeline.
        services.AddScoped<Sandcastle.Application.Knowledge.IKnowledgeService,
            Sandcastle.Infrastructure.Knowledge.KnowledgeService>();
        services.AddHostedService<
            Sandcastle.Infrastructure.Knowledge.KnowledgeGroomer>();
        services.AddScoped<Sandcastle.Application.Identity.IScimTokenService,
            Sandcastle.Infrastructure.Identity.IdentityAdminService>();
        services.AddScoped<Sandcastle.Application.Identity.IIdentityAdminService,
            Sandcastle.Infrastructure.Identity.IdentityAdminService>();
        services.AddScoped<Sandcastle.Application.Identity.IIdpSyncService,
            Sandcastle.Infrastructure.Identity.IdentityAdminService>();
        // SPEC-59 RF-002: input guard (pattern sempre; llm opcional por
        // política/config).
        services.AddSingleton<
            Sandcastle.Infrastructure.Security.PatternInputGuard>();
        services.AddScoped<Sandcastle.Application.Security.IInputGuard,
            Sandcastle.Infrastructure.Security.InputGuardService>();
        services.AddHttpClient("input-guard");
        services.AddScoped<Sandcastle.Application.Identity.IOrgService,
            Sandcastle.Infrastructure.Identity.OrgService>();
        services.AddScoped<Sandcastle.Application.Identity.IInvitationService,
            Sandcastle.Infrastructure.Identity.InvitationService>();
        services.AddScoped<Sandcastle.Application.Identity.ISuperAdminService,
            Sandcastle.Infrastructure.Identity.SuperAdminService>();
        services.AddScoped<Sandcastle.Application.Identity.IAdminUserService,
            Sandcastle.Infrastructure.Identity.AdminUserService>();
        services.AddScoped<Sandcastle.Application.Identity.IProvisioningService,
            Sandcastle.Infrastructure.Identity.ProvisioningService>();
        services.AddScoped<Sandcastle.Application.Identity.IDeviceFlowService,
            Sandcastle.Infrastructure.Identity.DeviceFlowService>();
        services.AddScoped<Sandcastle.Application.Identity.IQuotaService,
            Sandcastle.Infrastructure.Identity.QuotaService>();
        services.AddScoped<Sandcastle.Application.Identity.IAuditService,
            Sandcastle.Infrastructure.Identity.AuditService>();
        services.AddScoped<Sandcastle.Application.Identity.IFeatureFlagService,
            Sandcastle.Infrastructure.Identity.FeatureFlagService>();
        services.AddScoped<Sandcastle.Application.Identity.IUsageService,
            Sandcastle.Infrastructure.Identity.UsageService>();
        // SPEC-147 RF-004: "Your Budget" — limite/consumo/restante por modelo.
        services.AddScoped<Sandcastle.Application.Identity.IUsageBudgetService,
            Sandcastle.Infrastructure.Identity.UsageBudgetService>();

        // SPEC-20: billing — enforcement, metering write-path e catálogo.
        services.Configure<Sandcastle.Infrastructure.Identity.BillingOptions>(
            configuration.GetSection(
                Sandcastle.Infrastructure.Identity.BillingOptions.Section));
        services.AddScoped<Sandcastle.Application.Identity.IPlanEnforcementService,
            Sandcastle.Infrastructure.Identity.PlanEnforcementService>();
        // SPEC-36 RF-002: write-path bufferado (Redis) + flush worker
        // quando multi-réplica; single-réplica grava direto no EF.
        if (CacheRegistration.RedisEnabled(configuration))
        {
            services.AddScoped<Sandcastle.Application.Identity.IUsageMeter,
                Sandcastle.Infrastructure.Identity.BufferedUsageMeter>();
            services.AddHostedService<
                Sandcastle.Infrastructure.Identity.UsageFlushWorker>();
        }
        else
        {
            services.AddScoped<Sandcastle.Application.Identity.IUsageMeter,
                Sandcastle.Infrastructure.Identity.UsageMeter>();
        }
        services.AddScoped<Sandcastle.Application.Identity.IPlanAdminService,
            Sandcastle.Infrastructure.Identity.PlanAdminService>();
        services.AddScoped<
            Sandcastle.Infrastructure.Observability.SupportBundleService>();
        services.AddSingleton<Sandcastle.Application.Identity.IAnalyticsService,
            Sandcastle.Infrastructure.Identity.NullAnalyticsService>();
        services.AddSingleton<
            Sandcastle.Infrastructure.Identity.Provisioning.IBrokerUserAdmin,
            Sandcastle.Infrastructure.Identity.Provisioning.NullBrokerUserAdmin>();

    }

    private static void RegisterMcp(
        IServiceCollection services, IConfiguration configuration)
    {
        // MCP (SPEC-07): settings CRUD + client singleton + OAuth + probe.
        services.Configure<Sandcastle.Infrastructure.Mcp.McpOptions>(
            configuration.GetSection(
                Sandcastle.Infrastructure.Mcp.McpOptions.SectionName));
        services.AddScoped<Sandcastle.Application.Mcp.IMcpSettingsService,
            Sandcastle.Infrastructure.Mcp.McpSettingsService>();
        services.AddScoped<Sandcastle.Application.Mcp.IMcpOAuthService,
            Sandcastle.Infrastructure.Mcp.McpOAuthService>();
        services.AddHttpClient("mcp-oauth");
        services.AddSingleton<
            Sandcastle.Infrastructure.Mcp.McpClientService>();
        services.AddSingleton<Sandcastle.Application.Mcp.IMcpClientService>(
            sp => sp.GetRequiredService<
                Sandcastle.Infrastructure.Mcp.McpClientService>());

    }

    private static void RegisterAgent(
        IServiceCollection services, IConfiguration configuration)
    {
        // Agent engine (SPEC-09): LLM provider + tool registry + runner host.
        services.Configure<Sandcastle.Application.Agent.AgentLlmOptions>(
            configuration.GetSection("AgentLlm"));
        services.PostConfigure<Sandcastle.Application.Agent.AgentLlmOptions>(o =>
        {
            // Chaves flat legadas (estilo upstream): LLM__MODEL/BASE_URL/API_KEY.
            o.Model = configuration["LLM:MODEL"] ?? o.Model;
            o.BaseUrl ??= configuration["LLM:BASE_URL"];
            o.ApiKey ??= configuration["LLM:API_KEY"];
        });
        services.AddHttpClient("agent-llm");
        services.AddSingleton<
            Sandcastle.Application.Agent.ITaskTrackerService,
            Sandcastle.Infrastructure.Agent.InMemoryTaskTrackerService>();
        services.AddSingleton<
            Sandcastle.Application.Agent.IToolRegistry,
            Sandcastle.Infrastructure.Agent.ToolRegistry>();
        // SPEC-123: deferred tool loading — estado por conversa.
        services.AddSingleton<
            Sandcastle.Application.Agent.IDeferredToolState,
            Sandcastle.Infrastructure.Agent.DeferredToolState>();
        // SPEC-23: catálogo OmniRoute + router por role (singletons —
        // cache compartilhado; seleção é stateless por chamada).
        services.AddMemoryCache();
        services.AddSingleton<
            Sandcastle.Application.Agent.IModelCatalog,
            Sandcastle.Infrastructure.Agent.Routing.OmniRouteModelCatalog>();
        services.AddSingleton<
            Sandcastle.Application.Agent.IModelRouter,
            Sandcastle.Infrastructure.Agent.Routing.ModelRouter>();
        // SPEC-22: context engine — meter/condenser/builder scoped por run.
        services.AddScoped<
            Sandcastle.Application.Agent.IContextMeter,
            Sandcastle.Infrastructure.Agent.Context.HeuristicContextMeter>();
        services.AddScoped<
            Sandcastle.Application.Agent.ICondenser,
            Sandcastle.Infrastructure.Agent.Context.LlmSummarizingCondenser>();
        services.AddScoped<
            Sandcastle.Infrastructure.Agent.Context.ConversationHistoryBuilder>();
        services.AddScoped<
            Sandcastle.Application.Agent.ICompactionService,
            Sandcastle.Infrastructure.Agent.Context.CompactionService>();
        // SPEC-90: system-prompt contributors (pipeline componível —
        // composer itera por Order e tolera falha individual).
        services.AddSingleton<
            Sandcastle.Application.Agent.IAgentPromptContributor,
            Sandcastle.Infrastructure.Agent.SystemPrompt
                .BaseAgentContributor>();
        services.AddSingleton<
            Sandcastle.Application.Agent.IAgentPromptContributor,
            Sandcastle.Infrastructure.Agent.SystemPrompt
                .AutonomyTiersContributor>();
        services.AddSingleton<
            Sandcastle.Application.Agent.IAgentPromptContributor,
            Sandcastle.Infrastructure.Agent.SystemPrompt
                .SddWorkflowContributor>();
        services.AddSingleton<
            Sandcastle.Application.Agent.IAgentPromptContributor,
            Sandcastle.Infrastructure.Agent.SystemPrompt
                .RepoContextContributor>();
        services.AddSingleton<
            Sandcastle.Application.Agent.IAgentPromptContributor,
            Sandcastle.Infrastructure.Agent.SystemPrompt
                .SkillsContributor>();
        services.AddSingleton<
            Sandcastle.Application.Agent.IAgentPromptContributor,
            Sandcastle.Infrastructure.Agent.SystemPrompt
                .MemoryDigestContributor>();
        services.AddSingleton<
            Sandcastle.Application.Agent.IAgentPromptContributor,
            Sandcastle.Infrastructure.Agent.SystemPrompt
                .DelegationPolicyContributor>();
        services.AddSingleton<
            Sandcastle.Infrastructure.Agent.SystemPrompt
                .SystemPromptComposer>();
        services.AddSingleton<Sandcastle.Infrastructure.Agent.AgentRunnerHost>();
        services.AddSingleton<Sandcastle.Application.Agent.IAgentRunnerHost>(
            sp => sp.GetRequiredService<
                Sandcastle.Infrastructure.Agent.AgentRunnerHost>());
        // SPEC-33 RF-001: security analyzer — pattern (default, local) ou
        // llm (opt-in, classifica via completion + LRU cache).
        services.AddSingleton<
            Sandcastle.Application.Agent.ISecurityAnalyzer>(sp =>
        {
            var o = sp.GetRequiredService<Microsoft.Extensions.Options
                .IOptions<Sandcastle.Application.Agent.AgentLlmOptions>>()
                .Value;
            var llmProvider = () => sp.GetRequiredService<
                Sandcastle.Application.Agent.IAgentLlmProvider>();
            var memCache = sp.GetRequiredService<
                Microsoft.Extensions.Caching.Memory.IMemoryCache>();
            return o.SecurityAnalyzer.ToLowerInvariant() switch
            {
                "llm" => new Sandcastle.Infrastructure.Agent.Security
                    .LlmSecurityAnalyzer(llmProvider(), memCache),
                // SPEC-123: juiz allow/deny/ask com prompt embutido.
                "judge" => new Sandcastle.Infrastructure.Agent.Security
                    .SmartPermissionJudge(llmProvider(), memCache),
                _ => (Sandcastle.Application.Agent.ISecurityAnalyzer)
                    new Sandcastle.Infrastructure.Agent.Security
                        .PatternSecurityAnalyzer(),
            };
        });
        // SPEC-125 RF-001: advisor (2º modelo opinando em ações risky) —
        // inerte quando Agent:AdvisorModel não configurado.
        services.AddSingleton<
            Sandcastle.Application.Agent.IAdvisorService>(sp =>
            new Sandcastle.Infrastructure.Agent.AdvisorService(
                sp.GetRequiredService<Sandcastle.Application.Agent
                    .IAgentLlmProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Options
                    .IOptions<Sandcastle.Application.Agent
                        .AgentLlmOptions>>()));
        // SPEC-48 RF-001: política de segurança por org — presets +
        // overrides, cache distribuído (TTL 60s) com invalidação no write.
        services.AddScoped<
            Sandcastle.Application.Agent.ISecurityPolicyService>(sp =>
            new Sandcastle.Infrastructure.Security.SecurityPolicyService(
                sp.GetRequiredService<Sandcastle.Infrastructure.Persistence
                    .AppDbContext>(),
                sp.GetService<Microsoft.Extensions.Caching.Distributed
                    .IDistributedCache>()));
        // SPEC-50 RF-001/002: BYOR LiteLLM — admin client tipado +
        // lifecycle de virtual keys (ciphertext DataProtection, uma
        // ativa por usuário). Off por default (LlmProxy:Mode=off).
        services.Configure<Sandcastle.Application.Agent.LlmProxyOptions>(
            configuration.GetSection("LlmProxy"));
        services.AddHttpClient("litellm");
        services.AddSingleton<
            Sandcastle.Application.Agent.ILiteLlmAdminClient,
            Sandcastle.Infrastructure.Agent.Routing.LiteLlmAdminClient>();
        services.AddScoped<
            Sandcastle.Application.Agent.IByorKeyService>(sp =>
            new Sandcastle.Infrastructure.Agent.Routing.ByorKeyService(
                sp.GetRequiredService<Sandcastle.Infrastructure.Persistence
                    .AppDbContext>(),
                sp.GetRequiredService<Sandcastle.Application.Agent
                    .ILiteLlmAdminClient>(),
                sp.GetRequiredService<Microsoft.Extensions.Options
                    .IOptions<Sandcastle.Application.Agent
                        .LlmProxyOptions>>(),
                sp.GetRequiredService<Microsoft.AspNetCore.DataProtection
                    .IDataProtectionProvider>(),
                sp.GetService<Microsoft.Extensions.Caching.Distributed
                    .IDistributedCache>()));
        // SPEC-134 RF-002: BYOK por usuário — CRUD + resolve por run
        // (user → BYOR → global); DataProtection no mesmo padrão BYOR.
        services.AddScoped<
            Sandcastle.Application.Agent.IUserLlmKeyService>(sp =>
            new Sandcastle.Infrastructure.Agent.Routing.UserLlmKeyService(
                sp.GetRequiredService<Sandcastle.Infrastructure.Persistence
                    .AppDbContext>(),
                sp.GetRequiredService<Microsoft.AspNetCore.DataProtection
                    .IDataProtectionProvider>(),
                sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
                sp.GetRequiredService<Microsoft.Extensions.Caching.Memory
                    .IMemoryCache>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging
                    .ILogger<Sandcastle.Infrastructure.Agent.Routing
                        .UserLlmKeyService>>()));
        // SPEC-33 RF-003: secret store criptografado (DataProtection) —
        // valores resolvem só na borda do sandbox.
        services.AddScoped<Sandcastle.Application.Contracts.ISecretStore,
            Sandcastle.Infrastructure.Identity.SecretStore>();
        // SPEC-28: registry in-memory de runs de subagentes (singleton —
        // compartilhado entre tools do mesmo escopo de processo).
        services.AddSingleton<Sandcastle.Application.Agent.ISubagentHost,
            Sandcastle.Infrastructure.Agent.Subagents.SubagentHost>();
        // SPEC-95: catálogo declarativo de agents (builtin < user < ws).
        services.AddSingleton<Sandcastle.Application.Agent.IAgentCatalog,
            Sandcastle.Infrastructure.Agent.Subagents
                .AgentCatalogService>();
        services.AddSingleton<Sandcastle.Application.Agent.IAgentLlmProvider>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options
                .IOptions<Sandcastle.Application.Agent.AgentLlmOptions>>();
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

                return new Sandcastle.Infrastructure.Agent.MockLlmProvider(opts);
            }

            return Microsoft.Extensions.DependencyInjection.ActivatorUtilities
                .CreateInstance<
                    Sandcastle.Infrastructure.Agent.OpenAiCompatLlmProvider>(sp);
        });

        // SPEC-18 RF-001: browser headless no worker — singleton compartilhado,
        // um browser context isolado por conversa.
        services.Configure<
            Sandcastle.Infrastructure.Agent.Browser.BrowserOptions>(
            configuration.GetSection(
                Sandcastle.Infrastructure.Agent.Browser.BrowserOptions.Section));
        services.AddScoped<Sandcastle.Application.BrowserProfiles.IBrowserProfileService,
            Sandcastle.Infrastructure.BrowserProfiles.BrowserProfileService>();
        services.AddSingleton<Sandcastle.Application.Agent.IBrowserService,
            Sandcastle.Infrastructure.Agent.Browser.PlaywrightBrowserService>();

        // SPEC-34 RF-001: bus de deltas efêmeros (runner→hub; nunca EF).
        // SPEC-36 RF-002: cross-réplica via bus de coordenação quando Redis.
        services.AddSingleton<
            Sandcastle.Infrastructure.Agent.Deltas.AgentActivityBus>();
        services.AddSingleton<
            Sandcastle.Application.Events.IAgentActivityBus>(sp =>
            CacheRegistration.RedisEnabled(configuration)
                ? new Sandcastle.Infrastructure.Agent.Deltas
                    .DistributedAgentActivityBus(
                        sp.GetRequiredService<Sandcastle.Infrastructure
                            .Agent.Deltas.AgentActivityBus>(),
                        sp.GetRequiredService<Sandcastle.Application
                            .Coordination.ICoordinationBus>(),
                        sp.GetRequiredService<Microsoft.Extensions.Logging
                            .ILogger<Sandcastle.Infrastructure.Agent.Deltas
                                .DistributedAgentActivityBus>>())
                : sp.GetRequiredService<Sandcastle.Infrastructure.Agent
                    .Deltas.AgentActivityBus>());
        // SPEC-34 RF-002: artifact store — workspace default, s3 opt-in.
        services.Configure<
            Sandcastle.Infrastructure.Artifacts.ArtifactOptions>(
            configuration.GetSection(
                Sandcastle.Infrastructure.Artifacts.ArtifactOptions.Section));
        services.AddSingleton<
            Sandcastle.Application.Artifacts.IArtifactStore>(sp =>
        {
            var o = sp.GetRequiredService<Microsoft.Extensions.Options
                .IOptions<Sandcastle.Infrastructure.Artifacts
                    .ArtifactOptions>>();
            return o.Value.Provider.Equals("s3",
                StringComparison.OrdinalIgnoreCase)
                ? new Sandcastle.Infrastructure.Artifacts.S3ArtifactStore(o,
                    sp.GetRequiredService<Microsoft.Extensions.Logging
                        .ILogger<Sandcastle.Infrastructure.Artifacts
                            .S3ArtifactStore>>())
                : (Sandcastle.Application.Artifacts.IArtifactStore)
                    new Sandcastle.Infrastructure.Artifacts
                        .WorkspaceArtifactStore(o,
                            sp.GetRequiredService<Microsoft.Extensions.Logging
                                .ILogger<Sandcastle.Infrastructure.Artifacts
                                    .WorkspaceArtifactStore>>());
        });
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