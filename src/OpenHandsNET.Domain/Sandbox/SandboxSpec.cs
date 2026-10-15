namespace OpenHandsNET.Domain.Sandbox;

/// <summary>
/// Declarative sandbox template: image, resource limits, env, mounts and
/// lifetime. Mirrors upstream <c>SandboxSpecInfo</c>.
/// </summary>
public sealed record SandboxSpec(
    Guid Id,
    string Name,
    string Image,
    int MemoryMb = 4096,
    double CpuLimit = 2.0,
    int TimeoutMinutes = 60,
    IReadOnlyDictionary<string, string>? Env = null,
    bool MountWorkspace = true,
    int PidsLimit = 512,
    string? Profile = null,
    // SPEC-38: opt-in public ports (preview) — empty by default.
    IReadOnlyList<int>? PublicPorts = null)
{
    /// <summary>Default dev image (git + bash + runtimes) until we publish our own.</summary>
    public const string DefaultImage = "nikolaik/python-nodejs:python3.13-nodejs22";

    /// <summary>Container working dir for the conversation workspace.</summary>
    public const string WorkspacePath = "/workspace";
}
