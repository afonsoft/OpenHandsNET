using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Application.Events;

/// <summary>Event with its owning conversation — unit of the global fan-out stream.</summary>
public sealed record EventEnvelope(Guid ConversationId, EventBase Event);