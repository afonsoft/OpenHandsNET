using Microsoft.AspNetCore.DataProtection;
using OpenHandsNET.Api.Endpoints;
using OpenHandsNET.Api.Hubs;
using OpenHandsNET.Api.Options;
using OpenHandsNET.Infrastructure;
using OpenHandsNET.Infrastructure.Persistence;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// OPENHANDSNET_Foo__Bar env vars -> Foo:Bar configuration sections.
builder.Configuration.AddEnvironmentVariables("OPENHANDSNET_");

// Serilog: two-stage bootstrap so startup failures are still logged.
builder.Services.AddSerilog((services, lc) => lc
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

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
            .AddEntityFrameworkCoreInstrumentation();
        if (otlpEnabled)
        {
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation();
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
});
builder.Services.AddSignalR().AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions.PropertyNamingPolicy =
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
});
builder.Services.AddSingleton<TerminalBridgeService>();
builder.Services.AddProblemDetails();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

app.UseSerilogRequestLogging();

// .NET 10: MapStaticAssets serves the WASM client files (incl. _framework)
// as endpoint-routed assets; UseBlazorFrameworkFiles is the legacy middleware
// and must NOT be combined with it (request reaches end of pipeline -> 500).
app.MapStaticAssets();

app.MapSystemEndpoints();
app.MapAppConversationEndpoints();
app.MapEventEndpoints();
app.MapSandboxEndpoints();
app.MapSettingsEndpoints();
app.MapMiscEndpoints();
app.MapWebhookEndpoints();
app.MapGitEndpoints();
app.MapSkillEndpoints();
app.MapHookEndpoints();
app.MapHub<ConversationHub>("/hubs/conversations");
app.MapHealthChecks("/api/v1/health");
app.MapHealthChecks("/api/v1/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

// SPA fallback: any non-API route serves the Blazor WASM shell.
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Entry point exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
