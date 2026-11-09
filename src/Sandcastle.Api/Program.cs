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
builder.Services.AddScoped<Sandcastle.Application.Insights.ISessionInsightService, Sandcastle.Infrastructure.Insights.SessionInsightService>();
builder.Services.AddScoped<Sandcastle.Application.Delegation.IDelegationService, Sandcastle.Infrastructure.Delegation.DelegationService>();
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