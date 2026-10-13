namespace OpenHandsNET.Application.Agent;

/// <summary>
/// LLM provider abstraction (SPEC-09 RF-001): streams chat completions with
/// function-calling support over an OpenAI-compatible endpoint.
/// </summary>
public interface IAgentLlmProvider
{
    /// <summary>Model identifier in use.</summary>
    string ModelId { get; }

    /// <summary>
    /// Streams one completion: text/tool-call deltas followed by a terminal
    /// <see cref="CompletionEnd"/> carrying usage counters.
    /// </summary>
    IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<ToolSchema> tools,
        LlmOptions options, CancellationToken ct);

    /// <summary>Provider model descriptor.</summary>
    Task<ModelInfo> GetModelInfoAsync(CancellationToken ct);
}
