namespace OpenHandsNET.Domain.Hooks;

/// <summary>Fixed lifecycle points where hooks run (SPEC-11 RF-004).</summary>
public enum HookEvent
{
    /// <summary>Before the agent loop starts.</summary>
    OnConversationStart,

    /// <summary>After a user message lands, before the LLM call.</summary>
    OnMessage,

    /// <summary>Before a tool handler executes.</summary>
    OnToolPre,

    /// <summary>After a tool observation is appended.</summary>
    OnToolPost,

    /// <summary>When the run finishes.</summary>
    OnFinish,
}

/// <summary>Hook actions (SPEC-11 RF-004).</summary>
public enum HookAction
{
    /// <summary>Emit a <c>HookExecutionEvent</c> (+ optional context message).</summary>
    EmitEvent,

    /// <summary>Run a command inside the sandbox; output goes to the event.</summary>
    RunCommand,

    /// <summary>POST the hook payload to a URL with retry.</summary>
    Webhook,
}

/// <summary>User/org-configured lifecycle hook.</summary>
public sealed record HookConfig
{
    public required Guid Id { get; init; }
    public string? OwnerId { get; init; }
    public string? OrgId { get; init; }
    public required string Name { get; init; }
    public required HookEvent Event { get; init; }
    public required HookAction Action { get; init; }

    /// <summary>
    /// Action payload: context text (emit_event), shell command
    /// (run_command) or target URL (webhook).
    /// </summary>
    public string? Payload { get; init; }

    /// <summary>
    /// When true a failing hook aborts the current step. Dangerous —
    /// can stall the loop; default false (SPEC-11 guardrails).
    /// </summary>
    public bool Blocking { get; init; }

    public bool Enabled { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; }
}