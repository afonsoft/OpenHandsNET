using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

using OpenHandsNET.Api.Endpoints;
using OpenHandsNET.Api.Hubs;
using OpenHandsNET.Api.Options;

using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

using Prometheus;

using Scalar.AspNetCore;

using Serilog;

namespace OpenHandsNET.Api;

/// <summary>
/// Seções de bootstrap do <c>Program.cs</c> — extraídas para manter o
/// top-level legível (Sonar S3776).
/// </summary>
internal static class ProgramParts
{
    /// <summary>Serilog (console + rolling file + OTLP) e OpenTelemetry.</summary>
    public static void AddObservability(WebApplicationBuilder builder)
    {
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

        // Observability (OTLP exporter only when an endpoint is configured).
        var otlpEndpoint = builder.Configuration["Observability:OtlpEndpoint"]
            is { Length: > 0 } ep ? ep : null;
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation()
                    .AddSource(OpenHandsNET.Infrastructure.Telemetry
                        .OpenNetTelemetry.ActivitySourceName);
                if (otlpEndpoint is not null)
                {
                    tracing.AddOtlpExporter(
                        o => o.Endpoint = new Uri(otlpEndpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(OpenHandsNET.Infrastructure.Telemetry
                        .OpenNetTelemetry.MeterName);
                if (otlpEndpoint is not null)
                {
                    metrics.AddOtlpExporter(
                        o => o.Endpoint = new Uri(otlpEndpoint));
                }
            });
    }

    /// <summary>
    /// SPEC-51 RF-001: contrato OpenAPI público — documento <c>v1</c> gerado
    /// dos endpoints vivos (MapHub e ExcludeFromDescription ficam de fora).
    /// O transformer garante o mínimo da SPEC: summary 1-linha em toda
    /// operação que ainda não tem um explícito.
    /// </summary>
    public static void AddOpenApiDoc(IServiceCollection services)
    {
        services.AddOpenApi("v1", o =>
        {
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Info.Title = "OpenHandsNET API";
                doc.Info.Version = "v1";
                doc.Info.Description =
                    "Public REST surface (/api/v1, /api/acp, /v1 " +
                    "OpenAI-compat gateway). Auth: cookie session or " +
                    "`Authorization: Bearer sk-*`.";
                // SPEC-80 RF-002: esquemas de auth + security global.
                doc.Components ??= new OpenApiComponents();
                doc.Components.SecuritySchemes ??=
                    new Dictionary<string, IOpenApiSecurityScheme>();
                doc.Components.SecuritySchemes["bearer"] =
                    new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        Description =
                            "API key `sk-*` (header `Authorization: Bearer`).",
                    };
                doc.Components.SecuritySchemes["cookie"] =
                    new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.ApiKey,
                        In = ParameterLocation.Cookie,
                        Name = "openhandsnet_auth",
                        Description = "Sessão de browser.",
                    };
                doc.Security =
                [
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference("bearer", doc)]
                            = [],
                    },
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference("cookie", doc)]
                            = [],
                    },
                ];
                return Task.CompletedTask;
            });
            o.AddOperationTransformer((op, ctx, _) =>
            {
                op.Summary ??= $"{ctx.Description.HttpMethod} " +
                    $"/{ctx.Description.RelativePath}";
                var meta = ctx.Description.ActionDescriptor?.EndpointMetadata;
                var anonymous =
                    meta?.OfType<IAllowAnonymous>().Any() == true;
                if (anonymous)
                {
                    // operações anônimas não herdam o security global.
                    op.Security = [];
                }
                else
                {
                    // SPEC-80 RF-002: default-deny — endpoints sem
                    // AllowAnonymous exigem usuário autenticado
                    // (ICurrentUserAccessor lança 401 ou
                    // RequireAuthorization/perm:* aplica policy), então o
                    // contrato documenta 401/403 em toda operação não-anônima.
                    op.Responses ??= new OpenApiResponses();
                    op.Responses.TryAdd("401",
                        new OpenApiResponse { Description = "Unauthorized" });
                    op.Responses.TryAdd("403",
                        new OpenApiResponse { Description = "Forbidden" });
                }
                return Task.CompletedTask;
            });
        });
    }

    /// <summary>
    /// Dual auth (SPEC-08 RF-002): cookie session (browser) + Bearer sk-*
    /// (API key), plus optional OIDC broker when Auth:Provider=oidc.
    /// "smart" dispatches on the Authorization header so
    /// RequireAuthorization() accepts either.
    /// </summary>
    public static void AddAuth(WebApplicationBuilder builder)
    {
        var authProvider = builder.Configuration["Auth:Provider"] ?? "none";
        var authBuilder = builder.Services.AddAuthentication("smart")
            .AddPolicyScheme("smart", "cookie or apikey", o =>
                o.ForwardDefaultSelector = ctx =>
                    ctx.Request.Headers.Authorization.ToString()
                        .StartsWith("Bearer sk-",
                            StringComparison.OrdinalIgnoreCase)
                        ? Auth.ApiKeyAuthenticationHandler.SchemeName
                        : AuthEndpoints.CookieScheme)
            .AddCookie(AuthEndpoints.CookieScheme, o =>
            {
                o.Cookie.Name = "openhandsnet_auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.Cookie.SameSite = SameSiteMode.Lax;
                // Chunked cookie parity (RF-001): >4KB sessions split into
                // .0,.1,... parts.
                o.CookieManager = new Microsoft.AspNetCore.Authentication
                    .Cookies.ChunkingCookieManager();
                o.ExpireTimeSpan = TimeSpan.FromDays(14);
                o.SlidingExpiration = true;
                // APIs return 401/403, not redirects.
                o.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode =
                        StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            })
            .AddScheme<Microsoft.AspNetCore.Authentication
                    .AuthenticationSchemeOptions,
                Auth.ApiKeyAuthenticationHandler>(
                Auth.ApiKeyAuthenticationHandler.SchemeName, _ => { });

        if (authProvider.Equals("oidc", StringComparison.OrdinalIgnoreCase))
        {
            authBuilder.AddOpenIdConnect("oidc",
                o => ConfigureOidc(o, builder.Configuration));
        }
    }

    private static void ConfigureOidc(
        OpenIdConnectOptions o, IConfiguration config)
    {
        o.Authority = config["Auth:Authority"];
        o.ClientId = config["Auth:ClientId"];
        o.ClientSecret = config["Auth:ClientSecret"];
        o.ResponseType = "code";
        o.UsePkce = true;
        o.SignInScheme = AuthEndpoints.CookieScheme;
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
            var users = ctx.HttpContext.RequestServices.GetRequiredService<
                OpenHandsNET.Application.Identity.IUserService>();
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
                            System.Security.Claims.ClaimTypes.Email,
                            user.Email),
                        new System.Security.Claims.Claim(
                            "auth_type", "cookie"),
                        new System.Security.Claims.Claim(
                            "idp_type", user.IdpType ?? "oidc"),
                    ],
                    AuthEndpoints.CookieScheme));
        };
    }

    /// <summary>Middleware pipeline (ordem importa).</summary>
    public static void UsePipeline(
        WebApplication app, IConfiguration config)
    {
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

        // .NET 10: MapStaticAssets serves the WASM client files (incl.
        // _framework) as endpoint-routed assets; UseBlazorFrameworkFiles is
        // the legacy middleware and must NOT be combined with it (request
        // reaches end of pipeline -> 500).
        app.MapStaticAssets().AllowAnonymous();

        // SPEC-94: espelho read-only de _framework sem extensão na URL —
        // boot do WASM sobrevive a proxies corporativos que bloqueiam
        // .wasm/.dat/.dll.
        app.MapFrameworkAssetsEndpoints();

        // SPEC-08 RF-013: deployment_mode surfaced on every response.
        var deploymentMode = config["DeploymentMode"] ?? "oss";
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["X-Deployment-Mode"] = deploymentMode;
            await next();
        });

        // SPEC-59 RF-001: IP access lists — 1º do pipeline (antes do auth).
        app.UseMiddleware<Middleware.EdgeAccessMiddleware>();

        app.UseAuthentication();
        app.UseAuthorization();
        // SPEC-36/59: rate limit por ator+bucket — depois do auth p/ claims.
        app.UseMiddleware<Middleware.ApiRateLimitMiddleware>();
        // SPEC-113 RF-003: habilita upgrade WS — router e o proxy
        // same-origin de surfaces transportam websockify/ttyd.
        app.UseWebSockets();
        // SPEC-38: sandbox host/path router (no-op em RoutingMode=internal).
        app.UseMiddleware<Routing.SandboxHostRouter>();

        // SPEC-56 NFR: rotas de share público nunca devem ser indexadas.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/share"))
            {
                ctx.Response.Headers["X-Robots-Tag"] = "noindex";
            }

            await next();
        });
    }

    /// <summary>Mapeamento de todos os endpoint groups + hubs + probes.</summary>
    public static void MapApi(WebApplication app, IConfiguration config)
    {
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
        // Probes k8s/docker — anônimos por definição (SPEC-97).
        app.MapHealthChecks("/api/v1/health").AllowAnonymous();
        app.MapHealthChecks("/api/v1/ready",
            new Microsoft.AspNetCore.Diagnostics.HealthChecks
                .HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains("ready"),
            }).AllowAnonymous();
        app.MapReadinessEndpoints();

        // SPEC-51 RF-002: documento vivo em /openapi/v1.json — o golden
        // snapshot em tests/ detecta drift de contrato em PR. Anônimo p/ o
        // fuzz nightly (schemathesis baixa sem credencial — SPEC-97).
        app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
        // SPEC-51 RF-003: explorer opt-in — default on em Development, off
        // em produção (Api:OpenApiExplorer=true para habilitar
        // explicitamente).
        if (config.GetValue<bool?>("Api:OpenApiExplorer")
            ?? app.Environment.IsDevelopment())
        {
            app.MapScalarApiReference().AllowAnonymous();
        }

        // Prometheus scrape endpoint (SPEC-13 RF-004): openhandsnet_*
        // counters.
        app.UseMetricServer("/metrics");

        // SPA fallback: any non-API route serves the Blazor WASM shell.
        // Anônimo — a tela de login precisa carregar sem sessão (SPEC-97).
        app.MapFallbackToFile("index.html").AllowAnonymous();
    }
}