using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent;

/// <summary>
/// Scripted provider for dev/test (SPEC-09 RF-001): pops one script step per
/// completion; when the script is exhausted it issues a <c>finish</c> call so
/// the loop always terminates. Never usable outside oss/dev unless
/// <c>AgentLlm:AllowMock</c>.
/// </summary>
public sealed class MockLlmProvider : IAgentLlmProvider
{
    private readonly Queue<JsonNode> _script;
    private int _completion;

    /// <summary>Creates a mock from a JSON script string.</summary>
    public MockLlmProvider(IOptions<AgentLlmOptions> options)
        : this(ParseScript(options.Value.MockScript))
    {
    }

    /// <summary>Creates a mock from parsed script nodes (tests).</summary>
    public MockLlmProvider(IEnumerable<JsonNode>? script = null)
    {
        _script = new Queue<JsonNode>(script ?? []);
    }

    /// <inheritdoc />
    public string ModelId => "mock-llm";

    /// <inheritdoc />
    public Task<ModelInfo> GetModelInfoAsync(CancellationToken ct) =>
        Task.FromResult(new ModelInfo(ModelId, "mock"));

    /// <inheritdoc />
    public async IAsyncEnumerable<ILlmStreamChunk> ChatStreamAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<ToolSchema> tools,
        LlmOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var step = _script.Count > 0 ? _script.Dequeue() : AutoFinish();
        var responseId = $"mock-{++_completion}";

        // SPEC-41 RF-001: script "reasoning" emite deltas no canal thinking.
        if (step["reasoning"]?.GetValue<string>() is { } think
            && think.Length > 0)
        {
            foreach (var word in think.Split(' '))
            {
                ct.ThrowIfCancellationRequested();
                yield return new ReasoningDelta(word + " ");
                await Task.Yield();
            }
        }

        if (step["text"]?.GetValue<string>() is { } text && text.Length > 0)
        {
            // Deltas por palavra — suficiente para exercitar o streaming path.
            foreach (var word in text.Split(' '))
            {
                ct.ThrowIfCancellationRequested();
                yield return new TextDelta(word + " ");
                await Task.Yield();
            }
        }

        var calls = step["tool_calls"]?.AsArray()
            ?? (step["tool"] is { } single ? new JsonArray(single.DeepClone()) : null);
        if (calls is not null)
        {
            for (var i = 0; i < calls.Count; i++)
            {
                var call = calls[i]!;
                var args = call["arguments"] switch
                {
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    { } node => node.ToJsonString(),
                    _ => "{}",
                };
                yield return new ToolCallDelta(
                    i,
                    call["id"]?.GetValue<string>() ?? $"call_{_completion}_{i}",
                    call["name"]!.GetValue<string>(),
                    args);
            }
        }

        yield return new CompletionEnd(
            new LlmUsage(PromptTokens: Estimate(messages), CompletionTokens: 10,
                CostUsd: 0m), "stop", responseId);
    }

    private static JsonNode AutoFinish() => JsonNode.Parse(
        """{"tool":{"name":"finish","arguments":{"final_message":"No further scripted steps."}}}""")!;

    private static int Estimate(IReadOnlyList<LlmMessage> messages) =>
        messages.Sum(m => (m.Content?.Length ?? 0) / 4) + 1;

    private static IEnumerable<JsonNode> ParseScript(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            yield break;
        }

        foreach (var node in JsonNode.Parse(json)!.AsArray())
        {
            yield return node!;
        }
    }
}