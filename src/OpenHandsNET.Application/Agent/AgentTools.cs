using System.Text.Json;

using OpenHandsNET.Application.Events;
using OpenHandsNET.Application.Sandbox;

namespace OpenHandsNET.Application.Agent;

/// <summary>
/// One executable agent tool: schema advertised to the LLM + handler running
/// against the conversation sandbox.
/// </summary>
public sealed record AgentTool(
    ToolSchema Schema,
    bool Risky,
    Func<ToolCallContext, JsonElement, CancellationToken, Task<ToolResult>> Handler);

/// <summary>Services available to a tool handler during one call.</summary>
public sealed record ToolCallContext(
    Guid ConversationId,
    Domain.Sandbox.Sandbox Sandbox,
    ISandboxExecutor Executor,
    IEventService Events,
    IServiceProvider Services,
    TimeSpan DefaultTimeout,
    string? ToolCallId = null,
    string? ActionId = null)
{
    /// <summary>
    /// Appends a run-activity event (SPEC-24): <c>command_output</c>,
    /// <c>tool_progress</c>, <c>files_changed</c>, <c>agent_diagnostic</c>…
    /// </summary>
    public async Task EmitAsync(Domain.Events.EventBase e, CancellationToken ct) =>
        await Events.AppendAsync(ConversationId, e, ct);
}

/// <summary>
/// Tool outcome: <paramref name="Observation"/> is the JSON payload appended as
/// <c>ObservationEvent.observation</c> and forwarded to the LLM as the tool
/// message content.
/// </summary>
public sealed record ToolResult(JsonElement Observation, bool IsError = false)
{
    /// <summary>Builds a successful result from an anonymous object.</summary>
    public static ToolResult Ok(object payload) =>
        new(JsonSerializer.SerializeToElement(payload, EventJsonOptions));

    /// <summary>Builds an error result.</summary>
    public static ToolResult Error(string message) =>
        new(JsonSerializer.SerializeToElement(new { error = message },
            EventJsonOptions), IsError: true);

    private static readonly JsonSerializerOptions EventJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

/// <summary>Catalog of built-in + dynamic (<c>mcp__*</c>) agent tools.</summary>
public interface IToolRegistry
{
    /// <summary>All static tools advertised to the model.</summary>
    IReadOnlyList<AgentTool> All { get; }

    /// <summary>Finds a tool by name; returns <see langword="null"/> if unknown.</summary>
    AgentTool? Find(string name);
}