using Microsoft.AspNetCore.DataProtection;

using OpenHandsNET.Api.Endpoints;
using OpenHandsNET.Api.Hubs;
using OpenHandsNET.Api.Options;
using OpenHandsNET.Infrastructure;
using OpenHandsNET.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// OPENHANDSNET_Foo__Bar env vars -> Foo:Bar configuration sections.
builder.Configuration.AddEnvironmentVariables("OPENHANDSNET_");

OpenHandsNET.Api.ProgramParts.AddObservability(builder);

// Options
builder.Services.Configure<OpenHandsNetOptions>(builder.Configuration);

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
if (OpenHandsNET.Infrastructure.Cache.CacheRegistration
    .RedisEnabled(builder.Configuration))
{
    signalr.AddStackExchangeRedis(
        builder.Configuration["Cache:Redis:ConnectionString"]!,
        o => o.Configuration.ChannelPrefix =
            new StackExchange.Redis.RedisChannel(
                builder.Configuration["Cache:Redis:InstanceName"] ?? "ohn:",
                StackExchange.Redis.RedisChannel.PatternMode.Literal));
}
builder.Services.AddSingleton<TerminalBridgeService>();
builder.Services.AddSingleton<OpenHandsNET.Api.Routing.PreviewTokenService>();
builder.Services.AddProblemDetails();
OpenHandsNET.Api.ProgramParts.AddOpenApiDoc(builder.Services);
// SPEC-36 RF-002: rate limit global (IDistributedCounter; off por default).
builder.Services.Configure<OpenHandsNET.Api.Middleware.ApiRateLimitOptions>(
    builder.Configuration.GetSection(
        OpenHandsNET.Api.Middleware.ApiRateLimitOptions.Section));

OpenHandsNET.Api.ProgramParts.AddAuth(builder);

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
    OpenHandsNET.Api.Policies.PermissionPolicyProvider>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
    OpenHandsNET.Api.Policies.PermissionHandler>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
    OpenHandsNET.Api.Policies.ScopeHandler>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler,
    OpenHandsNET.Api.Policies.PermissionResultHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<OpenHandsNET.Application.Common.Interfaces.IOutputCompactorService, OpenHandsNET.Infrastructure.Agent.OutputCompactorService>();
builder.Services.AddSingleton<OpenHandsNET.Application.Sandbox.ISandboxSnapshotProvider, OpenHandsNET.Infrastructure.Sandbox.FileSystemSandboxSnapshotProvider>();
builder.Services.AddScoped<OpenHandsNET.Application.Indexing.ICodeSearchStore, OpenHandsNET.Infrastructure.Indexing.EfCodeSearchStore>();
builder.Services.AddScoped<OpenHandsNET.Application.Indexing.IRepoIndexer, OpenHandsNET.Infrastructure.Indexing.RepoIndexer>();
builder.Services.AddScoped<OpenHandsNET.Application.Ask.IAskModeService, OpenHandsNET.Infrastructure.Ask.AskModeService>();
builder.Services.AddScoped<OpenHandsNET.Application.Insights.ISessionInsightService, OpenHandsNET.Infrastructure.Insights.SessionInsightService>();
builder.Services.AddScoped<OpenHandsNET.Application.Delegation.IDelegationService, OpenHandsNET.Infrastructure.Delegation.DelegationService>();
builder.Services.AddScoped<OpenHandsNET.Application.Identity.ICurrentUserAccessor,
    OpenHandsNET.Api.Auth.HttpCurrentUserAccessor>();

var app = builder.Build();

OpenHandsNET.Api.ProgramParts.UsePipeline(app, builder.Configuration);
OpenHandsNET.Api.ProgramParts.MapApi(app, builder.Configuration);

await app.RunAsync();

/// <summary>Entry point exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program
{
    private Program() { }
}