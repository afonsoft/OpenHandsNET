using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Infrastructure.Eventing;

/// <summary>
/// Persisted row for the append-only <c>events</c> table. The full serialized
/// event lives in <see cref="Payload"/> (JSON); indexed columns are denormalized
/// for filtering/ordering.
/// </summary>
public sealed class EventRecord
{
    /// <summary>Event id (ULID/UUID string) — PK.</summary>
    public required string Id { get; init; }

    /// <summary>Owning conversation.</summary>
    public required Guid ConversationId { get; init; }

    /// <summary>Discriminator value (e.g. <c>MessageEvent</c>).</summary>
    public required string Kind { get; init; }

    /// <summary>Event source (lowercase: agent/user/environment/hook).</summary>
    public required string Source { get; init; }

    /// <summary>UTC ticks — deterministic sortable form of the event timestamp.</summary>
    public required long TimestampTicks { get; init; }

    /// <summary>Parent event id in the conversation tree.</summary>
    public string? ParentId { get; init; }

    /// <summary>Full serialized event JSON (incl. <c>kind</c>).</summary>
    public required string Payload { get; init; }
}