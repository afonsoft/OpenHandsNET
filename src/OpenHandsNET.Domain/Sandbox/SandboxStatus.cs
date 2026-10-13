namespace OpenHandsNET.Domain.Sandbox;

/// <summary>
/// Sandbox lifecycle state — upstream parity
/// (<c>STARTING|RUNNING|PAUSED|ERROR|MISSING</c>).
/// </summary>
public enum SandboxStatus
{
    /// <summary>Container is being created/started.</summary>
    Starting,

    /// <summary>Container running and healthy.</summary>
    Running,

    /// <summary>Paused (timeout or user) — workspace preserved.</summary>
    Paused,

    /// <summary>Container exists but failed/unhealthy.</summary>
    Error,

    /// <summary>Record exists but container is gone.</summary>
    Missing,
}
