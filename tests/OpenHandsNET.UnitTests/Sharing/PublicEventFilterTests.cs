using System.Text.Json;
using OpenHandsNET.Infrastructure.Sharing;
using Shouldly;
using Xunit;

namespace OpenHandsNET.UnitTests.Sharing;

/// <summary>
/// SPEC-56 RF-003 — regressão de leak: um evento com secret jamais pode
/// aparecer no payload público; kinds internos nunca saem do servidor.
/// </summary>
public sealed class PublicEventFilterTests
{
    private readonly PublicEventFilter _filter = new();

    [Theory]
    [InlineData("StreamingDeltaEvent")]
    [InlineData("TokenEvent")]
    [InlineData("LLMCompletionLogEvent")]
    [InlineData("agent_diagnostic")]
    [InlineData("SystemPromptEvent")]
    [InlineData("CondensationRequest")]
    [InlineData("ACPToolCallEvent")]
    [InlineData("HookExecutionEvent")]
    public void Internal_Kinds_Are_Never_Published(string kind)
    {
        _filter.Filter(kind, """{"kind":"x","id":"e1"}""", null)
            .ShouldBeNull();
    }

    [Fact]
    public void Secret_Values_Are_Redacted_Recursively()
    {
        const string payload = """
            {"kind":"ObservationEvent","id":"e1","observation":
              {"text":"using token hunter2 to login",
               "nested":{"deep":"value hunter2 here"}}}
            """;

        var result = _filter.Filter(
            "ObservationEvent", payload, ["hunter2"]);

        result.ShouldNotBeNull();
        var text = result.Value.GetRawText();
        text.ShouldNotContain("hunter2");
        text.ShouldContain("***");
    }

    [Fact]
    public void Secret_Placeholders_Are_Redacted()
    {
        const string payload = """
            {"kind":"MessageEvent","id":"e1","llm_message":
              {"role":"user","content":[{"text":"key {{secret:GH_TOKEN}}"}]}}
            """;

        var result = _filter.Filter("MessageEvent", payload, null);

        result.ShouldNotBeNull();
        result.Value.GetRawText().ShouldNotContain("GH_TOKEN");
    }

    [Fact]
    public void Sensitive_Properties_Are_Dropped()
    {
        const string payload = """
            {"kind":"ConversationStateUpdateEvent","id":"e1","key":"runtime",
             "value":{"agent_server_url":"https://vm.internal:9090",
                      "session_api_key":"k-123","state":"ready"}}
            """;

        var result = _filter.Filter(
            "ConversationStateUpdateEvent", payload, null);

        result.ShouldNotBeNull();
        var text = result.Value.GetRawText();
        text.ShouldNotContain("vm.internal");
        text.ShouldNotContain("k-123");
        text.ShouldContain("ready");
    }

    [Fact]
    public void Token_Suffix_Properties_Dropped_But_Metrics_Kept()
    {
        const string payload = """
            {"kind":"LLMUsageEvent","id":"e1","github_token":"ghp_x",
             "prompt_tokens":10,"completion_tokens":20}
            """;

        var result = _filter.Filter("LLMUsageEvent", payload, null);

        result.ShouldNotBeNull();
        var text = result.Value.GetRawText();
        text.ShouldNotContain("ghp_x");
        text.ShouldContain("prompt_tokens");
        text.ShouldContain("completion_tokens");
    }

    [Fact]
    public void Normal_Events_Pass_Through()
    {
        const string payload = """
            {"kind":"MessageEvent","id":"e1","source":"agent",
             "llm_message":{"role":"assistant",
               "content":[{"type":"text","text":"hello"}]}}
            """;

        var result = _filter.Filter("MessageEvent", payload, null);

        result.ShouldNotBeNull();
        var root = result.Value;
        root.GetProperty("kind").GetString().ShouldBe("MessageEvent");
        root.GetProperty("source").GetString().ShouldBe("agent");
    }
}
