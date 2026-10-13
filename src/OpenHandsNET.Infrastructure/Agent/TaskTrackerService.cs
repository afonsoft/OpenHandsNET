using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Infrastructure.Agent;

/// <summary>
/// In-memory plan store (SPEC-09 MVP); the emitted
/// <c>ConversationStateUpdateEvent</c> is the durable record.
/// </summary>
public sealed class InMemoryTaskTrackerService(IServiceScopeFactory scopes)
    : ITaskTrackerService
{
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<TaskTrackerItem>> _plans = new();

    /// <inheritdoc />
    public async Task<ConversationStateUpdateEvent> SetPlanAsync(
        Guid conversationId, IReadOnlyList<TaskTrackerItem> items, CancellationToken ct)
    {
        _plans[conversationId] = items;
        var e = new ConversationStateUpdateEvent
        {
            Key = "plan",
            Value = JsonSerializer.SerializeToElement(items, EventJson.Options),
        };
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IEventService>()
            .AppendAsync(conversationId, e, ct);
        return e;
    }

    /// <inheritdoc />
    public IReadOnlyList<TaskTrackerItem> GetPlan(Guid conversationId) =>
        _plans.TryGetValue(conversationId, out var items) ? items : [];
}
