using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Eventing;

namespace OpenHandsNET.UnitTests.Events;

/// <summary>
/// In-process bus fan-out: conversation channel + global envelope stream
/// (SPEC-02 RF-004, dispatcher input).
/// </summary>
public class EventBusTests
{
    [Fact]
    public async Task Publish_Delivers_To_Conversation_And_Global_Subscribers()
    {
        var bus = new InMemoryEventBus();
        var conv = Guid.NewGuid();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var convSeen = new List<string>();
        var globalSeen = new List<Guid>();
        var readers = Task.WhenAll(
            Task.Run(async () =>
            {
                await foreach (var e in bus.SubscribeAsync(conv, cts.Token))
                {
                    convSeen.Add(e.Id);
                    if (convSeen.Count == 2) break;
                }
            }),
            Task.Run(async () =>
            {
                await foreach (var env in bus.SubscribeAllAsync(cts.Token))
                {
                    globalSeen.Add(env.ConversationId);
                    if (globalSeen.Count == 2) break;
                }
            }));

        var other = Guid.NewGuid();
        await bus.PublishAsync(conv, new PauseEvent(), cts.Token);
        await bus.PublishAsync(conv, new InterruptEvent(), cts.Token);
        await readers;

        convSeen.Distinct().Count().ShouldBe(2);
        globalSeen.ShouldBe([conv, conv]);
        globalSeen.ShouldNotContain(other);
    }
}
