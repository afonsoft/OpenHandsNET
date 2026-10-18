using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenHandsNET.Domain.Events;

/// <summary>
/// Reserved <see cref="EventBase.ParentId"/> sentinel marking a tree root.
/// No event <see cref="EventBase.Id"/> may equal it (upstream parity).
/// </summary>
public static class EventTree
{
    /// <summary>Reserved root sentinel (<c>__root__</c>).</summary>
    public const string RootParentId = "__root__";
}

/// <summary>
/// V1 event model — faithful to the upstream software-agent-sdk contract:
/// <c>kind</c> discriminator, ULID/UUID string ids, <c>source</c>, optional
/// <c>parent_id</c> tree linkage. Events are immutable and append-only.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[JsonDerivedType(typeof(ActionEvent), "ActionEvent")]
[JsonDerivedType(typeof(MessageEvent), "MessageEvent")]
[JsonDerivedType(typeof(ObservationEvent), "ObservationEvent")]
[JsonDerivedType(typeof(AgentErrorEvent), "AgentErrorEvent")]
[JsonDerivedType(typeof(UserRejectObservation), "UserRejectObservation")]
[JsonDerivedType(typeof(SystemPromptEvent), "SystemPromptEvent")]
[JsonDerivedType(typeof(TokenEvent), "TokenEvent")]
[JsonDerivedType(typeof(StreamingDeltaEvent), "StreamingDeltaEvent")]
[JsonDerivedType(typeof(ConversationStateUpdateEvent), "ConversationStateUpdateEvent")]
[JsonDerivedType(typeof(ConversationErrorEvent), "ConversationErrorEvent")]
[JsonDerivedType(typeof(Condensation), "Condensation")]
[JsonDerivedType(typeof(CondensationRequest), "CondensationRequest")]
[JsonDerivedType(typeof(CondensationSummaryEvent), "CondensationSummaryEvent")]
[JsonDerivedType(typeof(InterruptEvent), "InterruptEvent")]
[JsonDerivedType(typeof(PauseEvent), "PauseEvent")]
[JsonDerivedType(typeof(ServerErrorEvent), "ServerErrorEvent")]
[JsonDerivedType(typeof(AcpToolCallEvent), "ACPToolCallEvent")]
[JsonDerivedType(typeof(HookExecutionEvent), "HookExecutionEvent")]
[JsonDerivedType(typeof(LlmCompletionLogEvent), "LLMCompletionLogEvent")]
[JsonDerivedType(typeof(DecisionAutoEvent), "decision_auto")]
[JsonDerivedType(typeof(CommandOutputEvent), "command_output")]
[JsonDerivedType(typeof(ToolProgressEvent), "tool_progress")]
[JsonDerivedType(typeof(FilesChangedEvent), "files_changed")]
[JsonDerivedType(typeof(RunProgressEvent), "run_progress")]
[JsonDerivedType(typeof(WorkSummaryEvent), "work_summary")]
[JsonDerivedType(typeof(AgentDiagnosticEvent), "agent_diagnostic")]
[JsonDerivedType(typeof(PullRequestCreatedEvent), "pull_request_created")]
public abstract record EventBase
{
    /// <summary>Unique event id — ULID/UUID7 string (lexicographic == temporal order).</summary>
    public string Id { get; init; } = Ulid.NewUlid().ToString();

    /// <summary>Event timestamp (serialized as ISO-8601).</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Event origin: agent | user | environment | hook.</summary>
    public EventSource Source { get; init; }

    /// <summary>
    /// Parent event id in the conversation tree. <see langword="null"/> for the root or
    /// for legacy events; siblings share the same <c>parent_id</c>.
    /// </summary>
    public string? ParentId { get; init; }

    /// <summary>
    /// SPEC-34 RF-001: streaming item slot — durable events that supersede an
    /// ephemeral <c>streaming_delta</c> sequence carry its item_id so clients
    /// can drop the provisional slot (upstream DeltaFrame parity).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ItemId { get; init; }

    /// <summary>
    /// Event kind discriminator value (exact upstream string), resolved from the
    /// <see cref="JsonDerivedTypeAttribute"/> registration on <see cref="EventBase"/>.
    /// </summary>
    [JsonIgnore]
    public string EventKind => EventJson.KindOf(GetType());
}

/// <summary>
/// Shared JSON options for the V1 event contract (snake_case fields, kind discriminator).
/// </summary>
public static class EventJson
{
    private static readonly ConcurrentDictionary<Type, string> Kinds = new();

    /// <summary>Resolves the upstream <c>kind</c> discriminator string for an event type.</summary>
    public static string KindOf(Type t) => Kinds.GetOrAdd(t, static t =>
        typeof(EventBase)
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .Where(a => a.DerivedType == t)
            .Select(a => a.TypeDiscriminator as string)
            .FirstOrDefault(s => s is not null)
        ?? t.Name);

    /// <summary>Canonical serializer options for events.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };
}
