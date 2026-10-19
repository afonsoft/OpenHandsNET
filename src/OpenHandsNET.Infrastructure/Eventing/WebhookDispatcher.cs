using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Infrastructure.Eventing;

/// <summary>
/// POSTs appended events to configured webhook endpoints with retry/backoff.
/// Endpoints come from <c>Webhooks:Endpoints</c> (array of URLs); per-conversation
/// filtering via <c>Webhooks:ConversationIds</c> (empty = all).
/// </summary>
public sealed class WebhookDispatcher(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<WebhookDispatcher> logger)
{
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    /// <summary>Dispatches one event to every configured webhook; failures are logged, never thrown.</summary>
    public async Task DispatchAsync(EventEnvelope envelope, CancellationToken ct)
    {
        var endpoints = configuration.GetSection("Webhooks:Endpoints").Get<string[]>() ?? [];
        if (endpoints.Length == 0)
        {
            return;
        }

        var allowed = configuration.GetSection("Webhooks:ConversationIds").Get<string[]>() ?? [];
        if (allowed.Length > 0 && !allowed.Contains(envelope.ConversationId.ToString(), StringComparer.Ordinal))
        {
            return;
        }

        var payload = JsonSerializer.Serialize(envelope.Event, typeof(EventBase), EventJson.Options);
        foreach (var endpoint in endpoints)
        {
            await SendWithRetryAsync(endpoint, payload, envelope.Event.Id, ct);
        }
    }

    private async Task SendWithRetryAsync(string endpoint, string payload, string eventId, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("webhooks");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await client.PostAsync(
                    endpoint, new StringContent(payload, System.Text.Encoding.UTF8, "application/json"), ct);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                logger.LogWarning(
                    "Webhook {Endpoint} event {EventId} attempt {Attempt} -> {StatusCode}",
                    endpoint, eventId, attempt + 1, (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Webhook {Endpoint} event {EventId} attempt {Attempt} failed",
                    endpoint, eventId, attempt + 1);
            }

            if (attempt >= Backoff.Length)
            {
                logger.LogError("Webhook {Endpoint} event {EventId} exhausted retries", endpoint, eventId);
                return;
            }

            await Task.Delay(Backoff[attempt], ct);
        }
    }
}