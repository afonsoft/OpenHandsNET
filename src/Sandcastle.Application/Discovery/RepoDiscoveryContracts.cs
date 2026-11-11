using Sandcastle.Application.Sandbox;

namespace Sandcastle.Application.Discovery;

/// <summary>
/// SPEC-25 RF-001: manifest of repo-provided context discovered after clone
/// (conventional instruction files, repo skills, config, readme).
/// </summary>
public sealed record RepoContextManifest(
    IReadOnlyList<string> InstructionFiles,
    IReadOnlyList<RepoSkillRef> Skills,
    bool HasRepoConfig,
    string? ReadmePath);

/// <summary>A repo-versioned skill found under a conventional skills dir.</summary>
public sealed record RepoSkillRef(
    string Name, string Path, string? Description);

/// <summary>
/// Discovers repo-provided context inside the workspace via the sandbox
/// executor (SPEC-25 RF-001). Result is emitted as
/// <c>ConversationStateUpdateEvent(key="repo_context")</c>.
/// </summary>
public interface IRepoDiscoveryService
{
    /// <summary>
    /// Scans conventional paths under <paramref name="repoRoot"/>
    /// (default <c>/workspace/repo</c>; SPEC-146: cada repo montado em
    /// <c>/workspace/repos/{short}</c> recebe o próprio probe).
    /// </summary>
    Task<RepoContextManifest> DiscoverAsync(
        ISandboxExecutor executor,
        Domain.Sandbox.Sandbox sandbox, CancellationToken ct,
        string? repoRoot = null);
}