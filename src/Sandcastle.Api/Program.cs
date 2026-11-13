using Microsoft.AspNetCore.DataProtection;

using Sandcastle.Api.Endpoints;
using Sandcastle.Api.Hubs;
using Sandcastle.Api.Options;
using Sandcastle.Infrastructure;
using Sandcastle.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// SANDCASTLE_Foo__Bar env vars -> Foo:Bar configuration sections.
builder.Configuration.AddEnvironmentVariables("SANDCASTLE_");

// #218: clients/fuzz podem enviar headers com bytes não-UTF8 (o httpx da
// schemathesis codifica valores unicode em Latin-1). O parser default do
// Kestrel rejeita bytes UTF-8 inválidos com um 400 de transporte
// (connection: close, corpo vazio) ANTES do pipeline — nenhum middleware
// alcança, e a resposta viola o contrato problem+json documentado.
// Latin-1 é o obs-text clássico do HTTP/1.1: decodifica qualquer byte, o
// request chega à app e a resposta sai schema-conforme (4xx problem).
builder.WebHost.ConfigureKestrel(o =>
    o.RequestHeaderEncodingSelector = _ => System.Text.Encoding.Latin1);

Sandcastle.Api.ProgramParts.AddObservability(builder);

// Options
builder.Services.Configure<SandcastleOptions>(builder.Configuration);

// Infrastructure (EF Core + DatabaseMigrator + health checks).
builder.Services.AddInfrastructure(builder.Configuration);
// DataProtection keys persistidas no banco (SPEC-10: secrets cifrados at-rest).
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AppDbContext>();
// V1 wire contract: snake_case payloads (paridade upstream).
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy =
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(
            System.Text.Json.JsonNamingPolicy.SnakeCaseLower));
});
var signalr = builder.Services.AddSignalR().AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions.PropertyNamingPolicy =
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
});
// SPEC-36 RF-002: multi-réplica → backplane Redis/Valkey para fan-out
// dos hubs entre instâncias (fail-open configurado em Cache:*).
if (Sandcastle.Infrastructure.Cache.CacheRegistration
    .RedisEnabled(builder.Configuration))
{
    signalr.AddStackExchangeRedis(
        builder.Configuration["Cache:Redis:ConnectionString"]!,
        o => o.Configuration.ChannelPrefix =
            new StackExchange.Redis.RedisChannel(
                builder.Configuration["Cache:Redis:InstanceName"] ?? "sc:",
                StackExchange.Redis.RedisChannel.PatternMode.Literal));
}
builder.Services.AddSingleton<TerminalBridgeService>();
builder.Services.AddSingleton<Sandcastle.Api.Routing.PreviewTokenService>();
builder.Services.AddSingleton<Sandcastle.Api.Routing.SandboxSurfaceProxy>();
builder.Services.AddProblemDetails();
Sandcastle.Api.ProgramParts.AddOpenApiDoc(builder.Services);
// SPEC-36 RF-002: rate limit global (IDistributedCounter; off por default).
builder.Services.Configure<Sandcastle.Api.Middleware.ApiRateLimitOptions>(
    builder.Configuration.GetSection(
        Sandcastle.Api.Middleware.ApiRateLimitOptions.Section));

Sandcastle.Api.ProgramParts.AddAuth(builder);

// SPEC-97 RF-001: default-deny real — endpoints sem AllowAnonymous exigem
// usuário autenticado (fecha os 35 `ignored_auth` do fuzz #218). Endpoints
// públicos opt-in via .AllowAnonymous() ou metadata AllowAnonymous.
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new Microsoft.AspNetCore.Authorization
        .AuthorizationPolicyBuilder("smart")
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider,
    Sandcastle.Api.Policies.PermissionPolicyProvider>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
    Sandcastle.Api.Policies.PermissionHandler>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
    Sandcastle.Api.Policies.ScopeHandler>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler,
    Sandcastle.Api.Policies.PermissionResultHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Sandcastle.Application.Common.Interfaces.IOutputCompactorService, Sandcastle.Infrastructure.Agent.OutputCompactorService>();
builder.Services.AddSingleton<Sandcastle.Application.Sandbox.ISandboxSnapshotProvider, Sandcastle.Infrastructure.Sandbox.FileSystemSandboxSnapshotProvider>();
builder.Services.AddScoped<Sandcastle.Application.Indexing.ICodeSearchStore, Sandcastle.Infrastructure.Indexing.EfCodeSearchStore>();
builder.Services.AddScoped<Sandcastle.Application.Indexing.IRepoIndexer, Sandcastle.Infrastructure.Indexing.RepoIndexer>();
builder.Services.AddScoped<Sandcastle.Application.Ask.IAskModeService, Sandcastle.Infrastructure.Ask.AskModeService>();
// SPEC-145 RF-001/RF-002 — conteúdo do repo (clone remoto | local) +
// pipeline de wiki (service → fila in-process → runner LLM).
builder.Services.Configure<Sandcastle.Infrastructure.Indexing.IndexingOptions>(
    builder.Configuration.GetSection(
        Sandcastle.Infrastructure.Indexing.IndexingOptions.Section));
builder.Services.Configure<Sandcastle.Infrastructure.Wiki.WikiOptions>(
    builder.Configuration.GetSection(
        Sandcastle.Infrastructure.Wiki.WikiOptions.Section));
builder.Services.AddScoped<Sandcastle.Infrastructure.Indexing.LocalRepoContentProvider>();
builder.Services.AddScoped<Sandcastle.Infrastructure.Indexing.GitRepoContentProvider>();
builder.Services.AddScoped<Sandcastle.Infrastructure.Indexing.RepoContentProviderResolver>();
builder.Services.AddScoped<Sandcastle.Application.Indexing.IRepoContentProvider>(
    sp => sp.GetRequiredService<Sandcastle.Infrastructure.Indexing.RepoContentProviderResolver>());
builder.Services.AddSingleton<Sandcastle.Infrastructure.Wiki.WikiDispatcher>();
builder.Services.AddScoped<Sandcastle.Application.Wiki.IWikiDispatcher>(
    sp => sp.GetRequiredService<Sandcastle.Infrastructure.Wiki.WikiDispatcher>());
builder.Services.AddScoped<Sandcastle.Application.Wiki.IWikiRunner,
    Sandcastle.Infrastructure.Wiki.WikiRunner>();
builder.Services.AddScoped<Sandcastle.Application.Wiki.IWikiService,
    Sandcastle.Infrastructure.Wiki.WikiService>();
builder.Services.AddHostedService<Sandcastle.Infrastructure.Wiki.WikiRunWorker>();
builder.Services.AddScoped<Sandcastle.Application.Insights.ISessionInsightService, Sandcastle.Infrastructure.Insights.SessionInsightService>();
builder.Services.AddScoped<Sandcastle.Application.Delegation.IDelegationService, Sandcastle.Infrastructure.Delegation.DelegationService>();
// SPEC-143: code scans — service (CRUD/findings/fix), orquestrador real
// (partição → delegate read-only → coleta/dedup), fila + worker
// (também reconcilia remediation pr_open/merged).
builder.Services.Configure<Sandcastle.Infrastructure.Scans.ScanOptions>(
    builder.Configuration.GetSection(
        Sandcastle.Infrastructure.Scans.ScanOptions.Section));
builder.Services.AddSingleton<Sandcastle.Infrastructure.Scans.ScanDispatcher>();
builder.Services.AddScoped<Sandcastle.Application.Scans.IScanDispatcher>(
    sp => sp.GetRequiredService<Sandcastle.Infrastructure.Scans.ScanDispatcher>());
builder.Services.AddHostedService<Sandcastle.Infrastructure.Scans.ScanWorker>();
builder.Services.AddScoped<Sandcastle.Application.Scans.IScanService,
    Sandcastle.Infrastructure.Scans.ScanService>();
builder.Services.AddScoped<Sandcastle.Application.Scans.IScanOrchestrator,
    Sandcastle.Infrastructure.Scans.ScanOrchestrator>();
// SPEC-144: batch refactors — planner (grafo deps → lotes), executor
// (deps + concurrency → delegate children → PR/lote), fila + worker
// (também reconcilia pr_open/merged e feedback de PR).
builder.Services.Configure<Sandcastle.Infrastructure.Refactors.RefactorOptions>(
    builder.Configuration.GetSection(
        Sandcastle.Infrastructure.Refactors.RefactorOptions.Section));
builder.Services.AddSingleton<Sandcastle.Infrastructure.Refactors.RefactorDispatcher>();
builder.Services.AddScoped<Sandcastle.Application.Refactors.IRefactorDispatcher>(
    sp => sp.GetRequiredService<Sandcastle.Infrastructure.Refactors.RefactorDispatcher>());
builder.Services.AddHostedService<Sandcastle.Infrastructure.Refactors.RefactorWorker>();
builder.Services.AddScoped<Sandcastle.Application.Refactors.IRefactorService,
    Sandcastle.Infrastructure.Refactors.RefactorService>();
builder.Services.AddScoped<Sandcastle.Application.Refactors.IRefactorPlanner,
    Sandcastle.Infrastructure.Refactors.RefactorPlanner>();
builder.Services.AddScoped<Sandcastle.Application.Refactors.IBatchExecutor,
    Sandcastle.Infrastructure.Refactors.BatchExecutor>();
// SPEC-152: benchmark/eval harness — service (runs/results/scorecard +
// guardrails de custo), suite adapters (swe-lite/custom/terminal-bench-2),
// runner (sandbox efêmero → agente eval → checker → verdict) + worker.
builder.Services.Configure<Sandcastle.Application.Eval.EvalOptions>(
    builder.Configuration.GetSection(
        Sandcastle.Application.Eval.EvalOptions.Section));
builder.Services.AddSingleton<Sandcastle.Infrastructure.Eval.EvalDispatcher>();
builder.Services.AddScoped<Sandcastle.Application.Eval.IEvalDispatcher>(
    sp => sp.GetRequiredService<Sandcastle.Infrastructure.Eval.EvalDispatcher>());
builder.Services.AddHostedService<Sandcastle.Infrastructure.Eval.EvalWorker>();
builder.Services.AddScoped<Sandcastle.Application.Eval.IEvalService,
    Sandcastle.Infrastructure.Eval.EvalService>();
builder.Services.AddScoped<Sandcastle.Application.Eval.IEvalRunner,
    Sandcastle.Infrastructure.Eval.EvalRunner>();
// SPEC-152: EvalRunner paraleliza tasks — um DbContext por task
// (DbContext não é thread-safe); factory sobre as options scoped.
builder.Services.AddSingleton<Microsoft.EntityFrameworkCore
    .IDbContextFactory<Sandcastle.Infrastructure.Persistence
    .AppDbContext>>(sp => new Sandcastle.Infrastructure.Persistence
    .AppDbContextFactory(sp.GetRequiredService<
        Microsoft.EntityFrameworkCore.DbContextOptions<
        Sandcastle.Infrastructure.Persistence.AppDbContext>>()));
builder.Services.AddSingleton<Sandcastle.Application.Eval.IEvalSuiteAdapter,
    Sandcastle.Infrastructure.Eval.SweLiteSuiteAdapter>();
builder.Services.AddSingleton<Sandcastle.Application.Eval.IEvalSuiteAdapter,
    Sandcastle.Infrastructure.Eval.CustomJsonlSuiteAdapter>();
builder.Services.AddSingleton<Sandcastle.Application.Eval.IEvalSuiteAdapter,
    Sandcastle.Infrastructure.Eval.TerminalBench2SuiteAdapter>();
builder.Services.AddSingleton<Sandcastle.Application.Eval.IEvalSuiteResolver,
    Sandcastle.Infrastructure.Eval.EvalSuiteResolver>();
builder.Services.AddScoped<Sandcastle.Application.Identity.ICurrentUserAccessor,
    Sandcastle.Api.Auth.HttpCurrentUserAccessor>();

var app = builder.Build();

Sandcastle.Api.ProgramParts.UsePipeline(app, builder.Configuration);
Sandcastle.Api.ProgramParts.MapApi(app, builder.Configuration);

await app.RunAsync();

/// <summary>Entry point exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program
{
    private Program() { }
}