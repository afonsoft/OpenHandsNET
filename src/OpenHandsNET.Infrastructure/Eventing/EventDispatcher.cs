using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Events;

namespace OpenHandsNET.Infrastructure.Eventing;

/// <summary>
/// Hosted service draining the global event stream into the webhook dispatcher.
/// (SignalR fan-out hooks in here at SPEC-04.)
/// </summary>
public sealed class EventDispatcher(
    IEventBus bus,
    WebhookDispatcher webhooks,
    ILogger<EventDispatcher> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var envelope in bus.SubscribeAllAsync(stoppingToken))
        {
            try
            {
                await webhooks.DispatchAsync(envelope, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Event dispatch failed for event {EventId}", envelope.Event.Id);
            }
        }
    }
}
