using Microsoft.AspNetCore.DataProtection;

using OpenHandsNET.Api.Endpoints;
using OpenHandsNET.Api.Hubs;
using OpenHandsNET.Api.Options;
using OpenHandsNET.Infrastructure;
using OpenHandsNET.Infrastructure.Persistence;

using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

using Prometheus;

using Scalar.AspNetCore;

using Serilog;

var builder = WebApplication.CreateBuilder(args);

// OPENHANDSNET_Foo__Bar env vars -> Foo:Bar configuration sections.
builder.Configuration.AddEnvironmentVariables("OPENHANDSNET_");

// Serilog: two-stage bootstrap so startup failures are still logged.
// Sinks: console + rolling file + OTLP (quando endpoint configurado).
var serilogOtlp = builder.Configuration["Observability:OtlpEndpoint"];
builder.Services.AddSerilog((services, lc) =>
{
    lc.ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("service", "openhandsnet-api")
        .WriteTo.Console()
        .WriteTo.File("logs/openhandsnet-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14);
    if (!string.IsNullOrWhiteSpace(serilogOtlp))
    {
        lc.WriteTo.OpenTelemetry(o => o.Endpoint = serilogOtlp);
    }
});

// Options
builder.Services.Configure<OpenHandsNetOptions>(builder.Configuration);

// Observability (OTLP exporter only when an endpoint is configured).
var otlpEndpoint = builder.Configuration["Observability:OtlpEndpoint"];
var otlpEnabled = !string.IsNullOrWhiteSpace(otlpEndpoint);
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddSource(OpenHandsNET.Infrastructure.Telemetry
                .OpenNetTelemetry.ActivitySourceName);
        if (otlpEnabled)
        {
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter(OpenHandsNET.Infrastructure.Telemetry
                .OpenNetTelemetry.MeterName);
        if (otlpEnabled)
        {
            metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
        }
    });

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
// SPEC-51 RF-001: contrato OpenAPI público — documento `v1` gerado dos
// endpoints vivos (MapHub e ExcludeFromDescription ficam de fora).
// O transformer garante o mínimo da SPEC: summary 1-linha em toda
// operação que ainda não tem um explícito.
builder.Services.AddOpenApi("v1", o =>
{
    o.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Info.Title = "OpenHandsNET API";
        doc.Info.Version = "v1";
        doc.Info.Description =
            "Public REST surface (/api/v1, /api/acp, /v1 OpenAI-compat " +
            "gateway). Auth: cookie session or `Authorization: Bearer sk-*`.";
        return Task.CompletedTask;
    });
    o.AddOperationTransformer((op, ctx, _) =>
    {
        op.Summary ??= $"{ctx.Description.HttpMethod} " +
            $"/{ctx.Description.RelativePath}";
        return Task.CompletedTask;
    });
});
// SPEC-36 RF-002: rate limit global (IDistributedCounter; off por default).
builder.Services.Configure<OpenHandsNET.Api.Middleware.ApiRateLimitOptions>(
    builder.Configuration.GetSection(
        OpenHandsNET.Api.Middleware.ApiRateLimitOptions.Section));

// Dual auth (SPEC-08 RF-002): cookie session (browser) + Bearer sk-* (API key),
// plus optional OIDC broker when Auth:Provider=oidc. "smart" dispatches on the
// Authorization header so RequireAuthorization() accepts either.
var authProvider = builder.Configuration["Auth:Provider"] ?? "none";
var authBuilder = builder.Services.AddAuthentication("smart")
    .AddPolicyScheme("smart", "cookie or apikey", o =>
        o.ForwardDefaultSelector = ctx =>
            ctx.Request.Headers.Authorization.ToString()
                .StartsWith("Bearer sk-", StringComparison.OrdinalIgnoreCase)
                ? OpenHandsNET.Api.Auth.ApiKeyAuthenticationHandler.SchemeName
                : OpenHandsNET.Api.Endpoints.AuthEndpoints.CookieScheme)
    .AddCookie(OpenHandsNET.Api.Endpoints.AuthEndpoints.CookieScheme, o =>
    {
        o.Cookie.Name = "openhandsnet_auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.Cookie.SameSite = SameSiteMode.Lax;
        // Chunked cookie parity (RF-001): >4KB sessions split into .0,.1,... parts.
        o.CookieManager = new Microsoft.AspNetCore.Authentication.Cookies.ChunkingCookieManager();
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        // APIs return 401/403, not redirects.
        o.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,
        OpenHandsNET.Api.Auth.ApiKeyAuthenticationHandler>(
        OpenHandsNET.Api.Auth.ApiKeyAuthenticationHandler.SchemeName, _ => { });

if (authProvider.Equals("oidc", StringComparison.OrdinalIgnoreCase))
{
    authBuilder.AddOpenIdConnect("oidc", o =>
    {
        o.Authority = builder.Configuration["Auth:Authority"];
        o.ClientId = builder.Configuration["Auth:ClientId"];
        o.ClientSecret = builder.Configuration["Auth:ClientSecret"];
        o.ResponseType = "code";
        o.UsePkce = true;
        o.SignInScheme = OpenHandsNET.Api.Endpoints.AuthEndpoints.CookieScheme;
        o.CallbackPath = "/oauth/oidc/callback";
        o.SaveTokens = true;
        // JIT provisioning: resolve broker claims into a local user cookie.
        o.Events.OnTicketReceived = async ctx =>
        {
            var email = ctx.Principal?.FindFirst(
                System.Security.Claims.ClaimTypes.Email)?.Value
                ?? ctx.Principal?.FindFirst("preferred_username")?.Value;
            if (email is null)
            {
                ctx.HandleResponse();
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var users = ctx.HttpContext.RequestServices
                .GetRequiredService<OpenHandsNET.Application.Identity.IUserService>();
            var user = await users.GetOrCreateAsync(
                email,
                ctx.Principal?.FindFirst("name")?.Value,
                ctx.Principal?.FindFirst("idp_type")?.Value,
                ctx.HttpContext.RequestServices
                    .GetRequiredService<IConfiguration>()
                    .GetValue("MultiTenancy:Enabled", false));
            // Replace the IdP principal with our local user claims.
            ctx.Principal = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [
                        new System.Security.Claims.Claim(
                            System.Security.Claims.ClaimTypes.NameIdentifier,
                            user.UserId.ToString()),
                        new System.Security.Claims.Claim(
                            System.Security.Claims.ClaimTypes.Email, user.Email),
                        new System.Security.Claims.Claim("auth_type", "cookie"),
                        new System.Security.Claims.Claim(
                            "idp_type", user.IdpType ?? "oidc"),
                    ],
                    OpenHandsNET.Api.Endpoints.AuthEndpoints.CookieScheme));
        };
    });
}

builder.Services.AddAuthorization();
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

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

app.UseSerilogRequestLogging();

// SPEC-13 RF-004: correlation enrichers em cada log do request.
// (Middleware roda pré-routing → conversation_id vem do path.)
app.Use(async (ctx, next) =>
{
    using var tracePush = Serilog.Context.LogContext
        .PushProperty("trace_id", ctx.TraceIdentifier);
    var match = System.Text.RegularExpressions.Regex.Match(
        ctx.Request.Path.Value ?? "",
        @"/app-conversations/([0-9a-fA-F-]{36})",
        System.Text.RegularExpressions.RegexOptions.None,
        TimeSpan.FromMilliseconds(500));
    using var convPush = match.Success
        ? Serilog.Context.LogContext
            .PushProperty("conversation_id", match.Groups[1].Value)
        : null;
    await next();
});

// .NET 10: MapStaticAssets serves the WASM client files (incl. _framework)
// as endpoint-routed assets; UseBlazorFrameworkFiles is the legacy middleware
// and must NOT be combined with it (request reaches end of pipeline -> 500).
app.MapStaticAssets();

// SPEC-08 RF-013: deployment_mode surfaced on every response.
var deploymentMode = builder.Configuration["DeploymentMode"] ?? "oss";
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Deployment-Mode"] = deploymentMode;
    await next();
});

// SPEC-59 RF-001: IP access lists — 1º do pipeline (antes do auth).
app.UseMiddleware<OpenHandsNET.Api.Middleware.EdgeAccessMiddleware>();

app.UseAuthentication();
app.UseAuthorization();
// SPEC-36/59: rate limit por ator+bucket — depois do auth p/ claims.
app.UseMiddleware<OpenHandsNET.Api.Middleware.ApiRateLimitMiddleware>();
// SPEC-38: sandbox host/path router (no-op em RoutingMode=internal).
app.UseMiddleware<OpenHandsNET.Api.Routing.SandboxHostRouter>();

// SPEC-56 NFR: rotas de share público nunca devem ser indexadas.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/share"))
    {
        ctx.Response.Headers["X-Robots-Tag"] = "noindex";
    }

    await next();
});

app.MapSystemEndpoints();
app.MapAuthEndpoints();
app.MapKeysEndpoints();
app.MapSecretsEndpoints();
app.MapOrgEndpoints();
app.MapAdminEndpoints();
app.MapAppConversationEndpoints();
app.MapEventEndpoints();
app.MapSandboxEndpoints();
app.MapSettingsEndpoints();
app.MapMiscEndpoints();
app.MapUserEndpoints();
app.MapWebhookEndpoints();
app.MapGitEndpoints();
app.MapCheckpointEndpoints();
app.MapSandboxSnapshotEndpoints();
app.MapUploadEndpoints();
app.MapSkillEndpoints();
app.MapAutomationEndpoints();
app.MapHookEndpoints();
app.MapMcpEndpoints();
app.MapOpenAiGatewayEndpoints();
app.MapAcpEndpoints();
app.MapShareEndpoints();
app.MapScmEndpoints();
app.MapChatOpsEndpoints();
app.MapTicketEndpoints();
app.MapServiceUserEndpoints();
app.MapScimEndpoints();
app.MapEdgeEndpoints();
app.MapAskAndIndexEndpoints();
app.MapSessionInsightEndpoints();
app.MapDelegationEndpoints();
app.MapKnowledgeEndpoints();
app.MapBrowserProfileEndpoints();
app.MapHub<ConversationHub>("/hubs/conversations");
app.MapHealthChecks("/api/v1/health");
app.MapHealthChecks("/api/v1/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});
app.MapReadinessEndpoints();

// SPEC-51 RF-002: documento vivo em /openapi/v1.json — o golden
// snapshot em tests/ detecta drift de contrato em PR.
app.MapOpenApi("/openapi/{documentName}.json");
// SPEC-51 RF-003: explorer opt-in — default on em Development, off em
// produção (Api:OpenApiExplorer=true para habilitar explicitamente).
if (builder.Configuration.GetValue<bool?>("Api:OpenApiExplorer")
    ?? app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();
}

// Prometheus scrape endpoint (SPEC-13 RF-004): openhandsnet_* counters.
app.UseMetricServer("/metrics");

// SPA fallback: any non-API route serves the Blazor WASM shell.
app.MapFallbackToFile("index.html");

await app.RunAsync();

/// <summary>Entry point exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;