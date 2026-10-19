using System.Text.Json;
using System.Text.Json.Nodes;

using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.UnitTests.Events;

/// <summary>
/// Golden round-trip tests: every upstream V1 <c>kind</c> must deserialize to
/// the right record and re-serialize to an equivalent JSON document
/// (SPEC-02 RF-001/RF-005, AC-01).
/// </summary>
public class EventSerializationTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "Events", "Golden", "all-kinds.json");

    private static readonly Dictionary<string, Type> Expected = new()
    {
        ["ActionEvent"] = typeof(ActionEvent),
        ["MessageEvent"] = typeof(MessageEvent),
        ["ObservationEvent"] = typeof(ObservationEvent),
        ["AgentErrorEvent"] = typeof(AgentErrorEvent),
        ["UserRejectObservation"] = typeof(UserRejectObservation),
        ["SystemPromptEvent"] = typeof(SystemPromptEvent),
        ["TokenEvent"] = typeof(TokenEvent),
        ["StreamingDeltaEvent"] = typeof(StreamingDeltaEvent),
        ["ConversationStateUpdateEvent"] = typeof(ConversationStateUpdateEvent),
        ["ConversationErrorEvent"] = typeof(ConversationErrorEvent),
        ["Condensation"] = typeof(Condensation),
        ["CondensationRequest"] = typeof(CondensationRequest),
        ["CondensationSummaryEvent"] = typeof(CondensationSummaryEvent),
        ["InterruptEvent"] = typeof(InterruptEvent),
        ["PauseEvent"] = typeof(PauseEvent),
        ["ServerErrorEvent"] = typeof(ServerErrorEvent),
        ["ACPToolCallEvent"] = typeof(AcpToolCallEvent),
        ["HookExecutionEvent"] = typeof(HookExecutionEvent),
        ["LLMCompletionLogEvent"] = typeof(LlmCompletionLogEvent),
        ["command_output"] = typeof(CommandOutputEvent),
        ["tool_progress"] = typeof(ToolProgressEvent),
        ["files_changed"] = typeof(FilesChangedEvent),
        ["run_progress"] = typeof(RunProgressEvent),
        ["work_summary"] = typeof(WorkSummaryEvent),
        ["agent_diagnostic"] = typeof(AgentDiagnosticEvent),
        ["HumanIntervenedObservation"] = typeof(HumanIntervenedObservation),
    };

    [Fact]
    public void Fixture_Covers_All_26_Kinds()
    {
        var events = LoadFixture();
        events.Count.ShouldBe(26);
        events.Select(e => e["kind"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal)
            .SetEquals(Expected.Keys).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Kind_Round_Trip_Preserves_Payload(string kind, JsonNode fixture)
    {
        // Covers AC-01: deserialize -> concrete record, reserialize -> equivalent doc.
        var e = JsonSerializer.Deserialize<EventBase>(fixture.ToJsonString(), EventJson.Options);
        e.ShouldNotBeNull();
        e.GetType().ShouldBe(Expected[kind]);
        e.EventKind.ShouldBe(kind);

        var reserialized = JsonSerializer.SerializeToNode(e, typeof(EventBase), EventJson.Options)!;
        reserialized["kind"]!.GetValue<string>().ShouldBe(kind);
        JsonNode.DeepEquals(reserialized, Normalize(fixture))
            .ShouldBeTrue($"round-trip drift on {kind}: {reserialized.ToJsonString()}");
    }

    [Fact]
    public void Id_Default_Is_Ulid_And_Rejects_No_Reserved()
    {
        var e = new InterruptEvent();
        Ulid.TryParse(e.Id, out _).ShouldBeTrue();
        e.Id.ShouldNotBe(EventTree.RootParentId);
    }

    [Fact]
    public void Deserialization_Tolerates_Extra_Fields()
    {
        // Upstream backward-compat policy: unknown fields must never break load.
        var json = "{\"kind\":\"PauseEvent\",\"id\":\"1\",\"timestamp\":\"2026-01-01T00:00:00Z\"," +
            "\"source\":\"user\",\"future_field\":{\"x\":1}}";
        var e = JsonSerializer.Deserialize<EventBase>(json, EventJson.Options);
        e.ShouldBeOfType<PauseEvent>();
    }

    public static IEnumerable<object[]> AllKinds()
    {
        // Passa o nó RAW: STJ exige "kind" como primeira propriedade na leitura.
        foreach (var node in LoadFixture())
        {
            yield return [node["kind"]!.GetValue<string>(), node];
        }
    }

    private static List<JsonNode> LoadFixture() =>
        JsonNode.Parse(File.ReadAllText(FixturePath))!.AsArray()
            .Select(n => n!).ToList();

    private static JsonNode Normalize(JsonNode node)
    {
        // Ordena propriedades para comparação estável (ordem de campos não é contrato).
        if (node is JsonObject obj)
        {
            var sorted = new JsonObject();
            foreach (var kv in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sorted[kv.Key] = kv.Value is null ? null : Normalize(kv.Value.DeepClone());
            }

            return sorted;
        }

        if (node is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr)
            {
                copy.Add(item is null ? null : Normalize(item.DeepClone()));
            }

            return copy;
        }

        return node.DeepClone();
    }
}