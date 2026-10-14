using System.Net;
using System.Net.Http.Json;
using Docker.DotNet;
using Microsoft.Extensions.Logging;

namespace OpenHandsNET.Infrastructure.Sandbox;

/// <summary>
/// SPEC-17 RF-003: HTTP client para o <c>openhandsnet-sandbox-agent</c> que
/// roda como PID 1 no container. Alcança o agente pelo IP na bridge Docker
/// (default) ou por porta publicada em <c>127.0.0.1</c> quando
/// <c>Sandbox:PublishAgentPort=true</c> (Docker Desktop/daemon remoto).
/// </summary>
public sealed class SandboxAgentClient(
    IDockerClient docker, IHttpClientFactory httpFactory,
    ILogger<SandboxAgentClient> logger)
{
    private const int AgentPort = 8000;

    /// <summary>Base URL do agente — null quando inalcançável (sem ip/sem agente).</summary>
    public async Task<Uri?> ResolveBaseUrlAsync(
        string containerId, CancellationToken ct)
    {
        try
        {
            var inspect = await docker.Containers
                .InspectContainerAsync(containerId, ct);
            // Porta publicada tem precedência (daemon remoto/Desktop).
            var ports = inspect.NetworkSettings?.Ports;
            if (ports is not null &&
                ports.TryGetValue($"{AgentPort}/tcp", out var bindings) &&
                bindings?.Count > 0 &&
                int.TryParse(bindings[0].HostPort, out var hostPort))
            {
                return new Uri($"http://127.0.0.1:{hostPort}");
            }

            var nets = inspect.NetworkSettings?.Networks;
            if (nets is not null)
            {
                foreach (var net in nets.Values)
                {
                    if (!string.IsNullOrEmpty(net.IPAddress))
                    {
                        return new Uri($"http://{net.IPAddress}:{AgentPort}");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "agent base-url resolve failed for {ContainerId}",
                containerId[..12]);
        }

        return null;
    }

    /// <summary>Probe <c>GET /alive</c> — 2s, null quando sem agente.</summary>
    public async Task<bool> ProbeAliveAsync(Uri baseUrl, CancellationToken ct) =>
        (await ProbeAliveV2Async(baseUrl, ct)) is not null;

    /// <summary>
    /// Probe <c>GET /alive</c> retornando o payload (SPEC-24 RF-006):
    /// <c>agent_version</c>, <c>state</c>, <c>uptime_s</c>, <c>sessions</c>.
    /// Null quando inalcançável.
    /// </summary>
    public async Task<AgentAliveInfo?> ProbeAliveV2Async(
        Uri baseUrl, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource
                .CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var http = httpFactory.CreateClient("sandbox-agent");
            var res = await http.GetAsync(
                new Uri(baseUrl, "/alive"), timeout.Token);
            if (res.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            var body = await res.Content.ReadFromJsonAsync<AliveBody>(ct);
            return new AgentAliveInfo(
                body?.AgentVersion ?? 1,
                body?.State ?? "unknown",
                body?.UptimeS ?? 0,
                body?.Sessions ?? 0);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Payload de <c>GET /alive</c> v2.</summary>
    public sealed record AgentAliveInfo(
        int AgentVersion, string State, long UptimeS, int Sessions);

    private sealed record AliveBody(
        [property: System.Text.Json.Serialization.JsonPropertyName("agent_version")]
        int? AgentVersion,
        [property: System.Text.Json.Serialization.JsonPropertyName("state")]
        string? State,
        [property: System.Text.Json.Serialization.JsonPropertyName("uptime_s")]
        long? UptimeS,
        [property: System.Text.Json.Serialization.JsonPropertyName("sessions")]
        int? Sessions);

    /// <summary><c>POST /api/init</c> — ativação dormant→ready (bootstrap key).</summary>
    public async Task<bool> InitAsync(Uri baseUrl, string initKey,
        string sessionKey, string? workspace,
        IReadOnlyDictionary<string, string>? env, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient("sandbox-agent");
            using var req = new HttpRequestMessage(HttpMethod.Post,
                new Uri(baseUrl, "/api/init"))
            {
                Content = JsonContent.Create(new
                {
                    session_key = sessionKey,
                    workspace,
                    env,
                }),
            };
            req.Headers.Add("X-Init-API-Key", initKey);
            var res = await http.SendAsync(req, ct);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "agent init failed on {BaseUrl}", baseUrl);
            return false;
        }
    }
}
