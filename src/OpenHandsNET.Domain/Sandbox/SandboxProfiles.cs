namespace OpenHandsNET.Domain.Sandbox;

/// <summary>
/// Named sandbox resource profiles (SPEC-14 RF-001). Values are upper bounds
/// applied to HostConfig — not reservations.
/// </summary>
public static class SandboxProfiles
{
    /// <summary>Profile name for the platform default.</summary>
    public const string Default = "medium";

    /// <summary>Resolved limits for a profile.</summary>
    public sealed record ProfileLimits(int MemoryMb, double CpuLimit, int PidsLimit);

    /// <summary>All valid profile names.</summary>
    public static readonly string[] Names = ["small", "medium", "large"];

    /// <summary>
    /// Resolve a profile name to limits (null when unknown). Defaults follow
    /// the upstream sizing guide tiers:
    /// small=1C/2G, medium=2C/4G (current implicit default), large=4C/8G.
    /// </summary>
    public static ProfileLimits? Resolve(string? profile) => profile switch
    {
        "small" => new ProfileLimits(MemoryMb: 2048, CpuLimit: 1.0, PidsLimit: 256),
        "medium" => new ProfileLimits(MemoryMb: 4096, CpuLimit: 2.0, PidsLimit: 512),
        "large" => new ProfileLimits(MemoryMb: 8192, CpuLimit: 4.0, PidsLimit: 1024),
        _ => null,
    };

    /// <summary>True when the name is a known profile.</summary>
    public static bool IsValid(string? profile) => Resolve(profile) is not null;
}
