using OpenHandsNET.Domain.Hooks;

namespace OpenHandsNET.Application.Hooks;

/// <summary>Input passed to a hook firing.</summary>
public sealed record HookContext
{
    public required Guid ConversationId { get; init; }

    /// <summary>Sandbox-bound executor used by <c>run_command</c> hooks.</summary>
    public Domain.Sandbox.Sandbox? Sandbox { get; init; }

    /// <summary>Tool/action context (on_tool_pre/on_tool_post).</summary>
    public string? ToolName { get; init; }
    public string? ActionId { get; init; }

    /// <summary>Message context (on_message).</summary>
    public string? MessageId { get; init; }
    public string? MessageText { get; init; }

    /// <summary>Structured extras serialized into the event payload.</summary>
    public System.Text.Json.JsonElement? HookInput { get; init; }

    /// <summary>DI scope — resolves the sandbox executor for run_command.</summary>
    public IServiceProvider? Services { get; init; }
}

/// <summary>Executes configured hooks at fixed lifecycle points (SPEC-11 RF-004).</summary>
public interface IHookService
{
    /// <summary>Hook config CRUD.</summary>
    Task<HookConfig> CreateAsync(HookConfig config, CancellationToken ct);
    Task<IReadOnlyList<HookConfig>> ListAsync(string? ownerId, CancellationToken ct);
    Task<HookConfig?> UpdateAsync(
        Guid id, string? ownerId, UpdateHookRequest update, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, string? ownerId, CancellationToken ct);

    /// <summary>
    /// Runs every enabled hook bound to <paramref name="point"/>. Emits one
    /// <c>HookExecutionEvent</c> per execution; failures never throw unless
    /// the hook is <c>blocking</c>.
    /// </summary>
    /// <returns>True when a blocking hook stopped the step.</returns>
    Task<bool> RunAsync(
        HookEvent point, HookContext context, CancellationToken ct);
}

/// <summary>Mutable hook fields.</summary>
public sealed record UpdateHookRequest(
    string? Name,
    HookEvent? Event,
    HookAction? Action,
    string? Payload,
    bool? Blocking,
    bool? Enabled);