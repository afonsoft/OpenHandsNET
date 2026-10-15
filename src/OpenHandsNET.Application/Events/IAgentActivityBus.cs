namespace OpenHandsNET.Application.Events;

/// <summary>
/// SPEC-34 RF-001: one ephemeral streaming frame — text deltas batched by
/// the runner (16ms/64B). NEVER persisted: travels runner → hub → client
/// only; the durable event sharing <see cref="ItemId"/> supersedes it.
/// </summary>
public sealed record DeltaFrame(
    string ItemId, long Seq, string Text);

/// <summary>
/// In-memory per-conversation pub/sub for ephemeral frames
/// (<see cref="DeltaFrame"/>). Singleton; subscribers get their own
/// channel; frames are dropped when nobody listens.
/// </summary>
public interface IAgentActivityBus
{
    /// <summary>Publishes a frame to every subscriber of the conversation.</summary>
    void Publish(Guid conversationId, DeltaFrame frame);

    /// <summary>Streams frames for the conversation until cancelled.</summary>
    IAsyncEnumerable<DeltaFrame> SubscribeAsync(
        Guid conversationId, CancellationToken ct);
}
