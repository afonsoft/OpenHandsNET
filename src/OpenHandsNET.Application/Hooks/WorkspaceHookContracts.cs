using OpenHandsNET.Domain.Hooks;

namespace OpenHandsNET.Application.Hooks;

/// <summary>
/// File-based lifecycle hook (SPEC-127): one entry parsed from a
/// <c>.openhands/hooks/*.hook.json</c> file discovered inside the sandbox
/// workspace (repo layer) or the sandbox home (user layer).
/// </summary>
public sealed record WorkspaceHookDefinition
{
    /// <summary>Canonical OHN event name (on_tool_pre, on_tool_post…).</summary>
    public required string Event { get; init; }

    /// <summary>Glob matched against the tool name; null = all tools.</summary>
    public string? Matcher { get; init; }

    /// <summary><c>command</c> (shell via sandbox executor) or
    /// <c>prompt</c> (context injected as a hook message).</summary>
    public required string Type { get; init; }

    /// <summary>Shell command or prompt text.</summary>
    public required string Command { get; init; }

    /// <summary>Exec timeout in seconds (command hooks; 1–300, default 60).</summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Opt-in veto: a failing command hook aborts the step.</summary>
    public bool Blocking { get; init; }

    /// <summary>File name inside the hooks dir (audit).</summary>
    public required string Name { get; init; }

    /// <summary>Discovery layer: <c>repo</c> or <c>user</c>.</summary>
    public required string Layer { get; init; }
}

/// <summary>
/// Discovers and executes <c>.openhands/hooks/*.hook.json</c> hooks
/// (SPEC-127). Definitions are cached per conversation — a workspace edit
/// mid-run takes effect on the next conversation (TTL-bounded).
/// </summary>
public interface IWorkspaceHookService
{
    /// <summary>
    /// Runs every file hook bound to <paramref name="point"/> whose matcher
    /// hits. Emits one <c>hook_fired</c> event per execution. Command hooks
    /// run inside the sandbox; prompt hooks inject a hook-sourced message.
    /// </summary>
    /// <returns>True when a blocking hook vetoed the step.</returns>
    Task<bool> RunAsync(
        HookEvent point, HookContext context, CancellationToken ct);
}