using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace OpenHandsNET.Infrastructure.Sandbox;

/// <summary>
/// SPEC-17 RF-003: HTTP client para o <c>openhandsnet-sandbox-agent</c> que
/// roda como PID 1 no container. URL resolution é do engine (SPEC-35 —
/// <see cref="Application.Sandbox.ISandboxEngine"/> resolve IP/porta); este
/// cliente só fala HTTP com a URL já resolvida.
/// </summary>
public sealed class SandboxAgentClient(
    IHttpClientFactory httpFactory,
    ILogger<SandboxAgentClient> logger)
{
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
