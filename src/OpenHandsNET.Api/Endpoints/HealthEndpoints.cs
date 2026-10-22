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
    private const string Healthy = "healthy";
    /// <summary>Maps the detailed readiness endpoint.</summary>
    public static IEndpointRouteBuilder MapReadinessEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/readiness", async (
            HealthCheckService health,
            IDockerClient docker,
            IOptions<AgentLlmOptions> llm,
            IOptions<OpenHandsNET.Sandboxing.SandboxOptions> sandbox,
            System.IServiceProvider sp,
            CancellationToken ct) =>
        {
            var report = await health.CheckHealthAsync(
                r => r.Tags.Contains("ready"), ct);

            // docker: ping real ao daemon (substrate local). SPEC-35: em
            // Provider=remote o substrate vive no runtime-api — o check é
            // o /healthz dele em vez do daemon local.
            string dockerStatus;
            if ((sandbox.Value.Provider ?? "local").Equals("kubernetes",
                    StringComparison.OrdinalIgnoreCase))
            {
                // Substrate é a API do cluster — não há daemon docker local
                // no pod (o engine usa IKubernetes). Check por-proxy: a
                // readiness real do provider kubernetes é exercitada via
                // sandbox pod, não aqui.
                dockerStatus = "n/a (provider=kubernetes)";
            }
            else if ((sandbox.Value.Provider ?? "local").Equals("remote",
                    StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var http = new HttpClient
                    {
                        BaseAddress = new Uri(sandbox.Value.RemoteBaseUrl!),
                        Timeout = TimeSpan.FromSeconds(3),
                    };
                    if (sandbox.Value.RemoteApiKey is { } rk)
                    {
                        http.DefaultRequestHeaders.Add("X-Runtime-Key", rk);
                    }

                    var res = await http.GetAsync("/healthz", ct);
                    dockerStatus = res.IsSuccessStatusCode
                        ? Healthy : $"runtime-api {(int)res.StatusCode}";
                }
                catch (Exception ex)
                {
                    dockerStatus = $"runtime-api unreachable: {ex.GetType().Name}";
                }
            }
            else
            {
                try
                {
                    await docker.System.PingAsync(ct);
                    dockerStatus = Healthy;
                }
                catch (Exception ex)
                {
                    dockerStatus = $"unreachable: {ex.GetType().Name}";
                }
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

            // SPEC-36: provider redis → reporta conectividade (fail-open:
            // degraded, nunca derruba o endpoint).
            var mux = sp.GetService<StackExchange.Redis.IConnectionMultiplexer>();
            checks["cache"] = mux is null ? "memory"
                : mux.IsConnected ? "ok" : "degraded";
            // kubernetes: o substrate é a API do cluster — a presença do pod
            // já prova agendamento; dockerStatus "n/a" não degrada.
            var healthy = report.Status == HealthStatus.Healthy &&
                          (dockerStatus == Healthy ||
                           dockerStatus.StartsWith("n/a",
                               StringComparison.Ordinal));

            // SPEC-19: egress allowlist → o proxy compartilhado precisa estar
            // running (ausente do payload quando o modo não é allowlist).
            if (sandbox.Value.EgressMode.Equals(
                    "allowlist", StringComparison.OrdinalIgnoreCase))
            {
                string proxyStatus;
                try
                {
                    var p = await docker.Containers.InspectContainerAsync(
                        OpenHandsNET.Sandboxing.EgressProxyService
                            .ProxyName, ct);
                    proxyStatus = p.State is { Running: true }
                        ? Healthy : p.State?.Status ?? "unknown";
                }
                catch (Docker.DotNet.DockerContainerNotFoundException)
                {
                    proxyStatus = "missing";
                }
                catch (Exception ex)
                {
                    proxyStatus = $"error: {ex.GetType().Name}";
                }

                checks["egress_proxy"] = proxyStatus;
                healthy &= proxyStatus == Healthy;
            }
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