namespace OpenHandsNET.Domain.Conversations;

/// <summary>
/// Start-task state machine — upstream parity:
/// <c>WORKING → WAITING_FOR_SANDBOX → PREPARING_REPOSITORY →
/// SETTING_UP_SKILLS → READY | ERROR</c>.
/// </summary>
public enum StartTaskStatus
{
    /// <summary>Accepted, queued.</summary>
    Working,

    /// <summary>Waiting on sandbox creation.</summary>
    WaitingForSandbox,

    /// <summary>Cloning/preparing the selected repository.</summary>
    PreparingRepository,

    /// <summary>Blueprint: SDK installers + .openhands/setup.sh (SPEC-17).</summary>
    SettingUpEnvironment,

    /// <summary>Installing skills/hooks.</summary>
    SettingUpSkills,

    /// <summary>Conversation usable.</summary>
    Ready,

    /// <summary>Failed (detail populated).</summary>
    Error,
}

/// <summary>
/// Asynchronous conversation-start task: <c>POST /app-conversations</c>
/// returns this instead of a ready conversation (upstream parity).
/// </summary>
public sealed record AppConversationStartTask(
    Guid Id,
    StartTaskStatus Status,
    Guid? AppConversationId,
    Guid? SandboxId,
    string? Detail,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);