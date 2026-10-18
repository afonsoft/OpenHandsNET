using System.Text.Json;

namespace OpenHandsNET.Application.Agent;

/// <summary>
/// Canonical chat message sent to/received from the LLM provider
/// (OpenAI-compatible wire shape: role + content + tool calls).
/// </summary>
public sealed record LlmMessage(
    string Role,
    string? Content,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? ToolCallId = null)
{
    /// <summary>User-role message shorthand.</summary>
    public static LlmMessage User(string text) => new("user", text);

    /// <summary>Assistant-role message shorthand.</summary>
    public static LlmMessage Assistant(string text) => new("assistant", text);

    /// <summary>Assistant tool-call request message.</summary>
    public static LlmMessage AssistantToolCall(
        string? text, IReadOnlyList<LlmToolCall> calls) =>
        new("assistant", text, calls);

    /// <summary>Tool result message bound to a call id.</summary>
    public static LlmMessage ToolResult(string toolCallId, string content) =>
        new("tool", content, ToolCallId: toolCallId);
}

/// <summary>A single function/tool call requested by the model.</summary>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>Streaming unit produced by <see cref="IAgentLlmProvider.ChatStreamAsync"/>.</summary>
public abstract record LlmStreamChunk;

/// <summary>Incremental text delta.</summary>
public sealed record TextDelta(string Text) : LlmStreamChunk;

/// <summary>
/// SPEC-41 RF-001: incremental reasoning delta (thinking models) — travels
/// a parallel channel, never merged into content.
/// </summary>
public sealed record ReasoningDelta(string Text) : LlmStreamChunk;

/// <summary>
/// Incremental tool-call delta (OpenAI streaming shape): fragments accumulate
/// per <see cref="ToolCallDelta.Index"/> until the completion ends.
/// </summary>
public sealed record ToolCallDelta(
    int Index, string? Id, string? Name, string? ArgumentsFragment) : LlmStreamChunk;

/// <summary>Terminal chunk of one completion: usage counters + finish reason.</summary>
public sealed record CompletionEnd(
    LlmUsage Usage, string? FinishReason, string ResponseId) : LlmStreamChunk;

/// <summary>Token accounting for one completion.</summary>
public sealed record LlmUsage(int PromptTokens, int CompletionTokens, decimal? CostUsd);

/// <summary>
/// Per-request generation options. <paramref name="Byor"/> (SPEC-50)
/// overrides endpoint/credential for a single call — the virtual key is
/// in-memory only, never logged or persisted in cleartext.
/// </summary>
public sealed record LlmOptions(
    string? Model = null,
    double? Temperature = null,
    int? MaxTokens = null,
    ByorCallContext? Byor = null);

/// <summary>Tool schema advertised to the model (JSON Schema input).</summary>
public sealed record ToolSchema(
    string Name, string Description, JsonElement InputSchema);

/// <summary>Provider pricing (USD per 1M tokens), when the gateway advertises it.</summary>
public sealed record ModelPricing(decimal? InputPerMillion, decimal? OutputPerMillion);

/// <summary>Provider model descriptor.</summary>
public sealed record ModelInfo(
    string Id, string Provider, int? ContextWindow = null,
    ModelPricing? Pricing = null);
