using OpenHandsNET.Application.Memory;

namespace OpenHandsNET.Application.Agent;

/// <summary>
/// Shared context for system-prompt contributors (SPEC-90 RF-001): carries
/// run/conversation inputs plus memoized factories so expensive lookups
/// (sandbox probe, memory digest) run at most once per prompt build.
/// Application layer — no Infrastructure/EF references.
/// </summary>
public sealed class AgentPromptContext
{
    /// <summary>Repo cloned into the workspace, when selected.</summary>
    public string? SelectedRepository { get; init; }

    /// <summary>Tool schemas enabled for this run.</summary>
    public required IReadOnlyList<ToolSchema> Tools { get; init; }

    /// <summary>Skills resolved for the conversation (db + repo).</summary>
    public IReadOnlyList<Domain.Skills.Skill>? Skills { get; init; }

    /// <summary>
    /// Rendered <c>## Repository context</c> markdown (SPEC-25) — quoted
    /// repo instructions, never privileged.
    /// </summary>
    public string? RepoContext { get; init; }

    /// <summary>Cancellation of the run.</summary>
    public required CancellationToken Ct { get; init; }

    /// <summary>
    /// Available <c>subagent_type</c> values (presets today; declarative
    /// catalog merges in via SPEC-95). Null/empty = delegation policy
    /// contributor stays silent.
    /// </summary>
    public IReadOnlyList<string>? SubagentCatalog { get; init; }

    /// <summary>
    /// Memoized org-memory digest loader (SPEC-30 RF-003). Null = digest
    /// not available standalone (already merged into
    /// <see cref="RepoContext"/>).
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<MemoryEntry>>>?
        MemoryDigestFactory
    { get; init; }

    /// <summary>
    /// Memoized SDD-surface probe — true when the workspace contains
    /// <c>.specs/</c> or <c>docs/specs/</c>. Typically implemented by
    /// exec'ing inside the conversation sandbox; null = probe
    /// unavailable (auto mode falls back to the workdir filesystem).
    /// </summary>
    public Func<CancellationToken, Task<bool>>? SddProbeFactory { get; init; }

    /// <summary>
    /// Memoized host-side workspace path, when the run executes on the
    /// host filesystem (empty when the workspace lives in a container).
    /// </summary>
    public Func<CancellationToken, Task<string>>? WorkdirFactory { get; init; }

    private Task<IReadOnlyList<MemoryEntry>>? _digestTask;
    private Task<bool>? _sddProbeTask;
    private Task<string>? _workdirTask;

    /// <summary>Whether task/subagent tools are enabled for this run.</summary>
    public bool HasTaskTools => Tools.Any(
        t => t.Name is "task" or "task_status" or "task_result");

    /// <summary>Org-memory digest resolved once (memoized).</summary>
    public Task<IReadOnlyList<MemoryEntry>> GetMemoryDigestAsync() =>
        _digestTask ??= MemoryDigestFactory?.Invoke(Ct)
            ?? Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

    /// <summary>SDD-surface probe resolved once (memoized).</summary>
    public Task<bool> GetSddSurfaceAsync() =>
        _sddProbeTask ??= SddProbeFactory?.Invoke(Ct)
            ?? Task.FromResult(false);

    /// <summary>Host workdir resolved once (memoized).</summary>
    public Task<string> GetWorkdirAsync() =>
        _workdirTask ??= WorkdirFactory?.Invoke(Ct)
            ?? Task.FromResult(string.Empty);
}

/// <summary>
/// Contributor of one system-prompt section (SPEC-90 RF-001): the old
/// static <c>SystemPromptBuilder</c> blocks became contributors with a
/// fixed <see cref="Order"/>; the composer iterates in ascending order
/// and concatenates sections. Resolved via DI as
/// <see cref="System.Collections.Generic.IEnumerable{T}"/>; a throwing
/// contributor is tolerated by the composer (section omitted + warning).
/// </summary>
public interface IAgentPromptContributor
{
    /// <summary>Position in the concatenation (lower first).</summary>
    int Order { get; }

    /// <summary>
    /// Produces the contributor's prompt sections (0..N strings, kept in
    /// returned order). Empty list = contributor does not apply.
    /// </summary>
    /// <param name="ctx">Run context.</param>
    /// <returns>Sections to concatenate.</returns>
    Task<IReadOnlyList<string>> ContributeAsync(AgentPromptContext ctx);
}