namespace OpenHandsNET.Domain.Sandbox;

/// <summary>
/// Sandbox lifecycle state — upstream parity
/// (<c>STARTING|RUNNING|PAUSED|STOPPED|ERROR|MISSING</c>).
/// </summary>
public enum SandboxStatus
{
    /// <summary>Container is being created/started.</summary>
    Starting,

    /// <summary>Container running and healthy.</summary>
    Running,

    /// <summary>Paused (timeout or user) — workspace preserved.</summary>
    Paused,

    /// <summary>
    /// Stopped after long pause (SPEC-16 RF-003) — processes dead, workspace
    /// and container fs preserved; resume via <c>docker start</c>.
    /// </summary>
    Stopped,

    /// <summary>Container exists but failed/unhealthy.</summary>
    Error,

    /// <summary>Record exists but container is gone.</summary>
    Missing,
}
