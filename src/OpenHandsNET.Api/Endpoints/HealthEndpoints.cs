using Docker.DotNet;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// <c>/api/v1/readiness</c> — readiness detalhado (SPEC-13 §5): executa os
/// checks <c>ready</c> e devolve o breakdown por componente (db, docker, llm).
/// </summary>
public static class HealthEndpoints
{
    /// <summary>Maps the detailed readiness endpoint.</summary>
    public static IEndpointRouteBuilder MapReadinessEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/readiness", async (
            HealthCheckService health,
            IDockerClient docker,
            IOptions<AgentLlmOptions> llm,
            CancellationToken ct) =>
        {
            var report = await health.CheckHealthAsync(
                r => r.Tags.Contains("ready"), ct);

            // docker: ping real ao daemon (sandbox substrate).
            string dockerStatus;
            try
            {
                await docker.System.PingAsync(ct);
                dockerStatus = "healthy";
            }
            catch (Exception ex)
            {
                dockerStatus = $"unreachable: {ex.GetType().Name}";
            }

            var llmStatus = string.IsNullOrEmpty(llm.Value.Provider) &&
                            string.IsNullOrEmpty(llm.Value.ApiKey)
                ? "mock"
                : "configured";

            var checks = new Dictionary<string, object>
            {
                ["db"] = report.Entries.TryGetValue("database", out var dbCheck)
                    ? dbCheck.Status.ToString().ToLowerInvariant()
                    : "unknown",
                ["docker"] = dockerStatus,
                ["llm"] = llmStatus,
            };
            var healthy = report.Status == HealthStatus.Healthy &&
                          dockerStatus == "healthy";
            return Results.Json(new
            {
                status = healthy ? "ready" : "degraded",
                checks,
                duration_ms = report.TotalDuration.TotalMilliseconds,
            }, statusCode: healthy ? 200 : 503);
        }).WithTags("Health").ExcludeFromDescription();
        return app;
    }
}
