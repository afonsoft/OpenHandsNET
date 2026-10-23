using OpenHandsNET.Application.Sandbox;

namespace OpenHandsNET.Application.Agent;

/// <summary>
/// Declarative sub-agent definition (SPEC-95 RF-001/002): a
/// <c>*.agent.md</c> file with minimal frontmatter
/// (<c>name</c>+<c>description</c> required, optional
/// <c>tools</c>/<c>model</c>) plus a markdown body that becomes the
/// child run's persona/system instructions.
/// </summary>
/// <param name="Name">Kebab-case id — catalog key (case-insensitive).</param>
/// <param name="Description">One line shown by <c>agent_catalog</c>.</param>
/// <param name="Tools">
/// Optional allowlist — can only <em>restrict</em> the child's toolset
/// (RF-005); <c>null</c> = inherit the general (non-reserved) set.
/// </param>
/// <param name="Model">Optional model override; <c>null</c> inherits.</param>
/// <param name="Body">Markdown instructions — child persona.</param>
/// <param name="Source">Provenance: <c>builtin|user|workspace</c>.</param>
/// <param name="FilePath">Origin path (empty for built-ins).</param>
public sealed record AgentDefinition(
    string Name,
    string Description,
    IReadOnlyList<string>? Tools,
    string? Model,
    string Body,
    string Source,
    string FilePath);

/// <summary>
/// Layered agent catalog (SPEC-95): effective set of
/// <see cref="AgentDefinition"/>s discoverable for a conversation —
/// built-in presets &lt; host user dir &lt; workspace dirs, same-name
/// entries overridden by the higher layer.
/// </summary>
public interface IAgentCatalog
{
    /// <summary>
    /// Lists the effective catalog. <paramref name="sandbox"/>/
    /// <paramref name="executor"/> enable the workspace layer; when
    /// either is null the catalog is built-in + user only.
    /// </summary>
    Task<IReadOnlyList<AgentDefinition>> ListAsync(
        Domain.Sandbox.Sandbox? sandbox,
        ISandboxExecutor? executor,
        CancellationToken ct);

    /// <summary>Catalog names only — the prompt-side listing.</summary>
    async Task<IReadOnlyList<string>> ListNamesAsync(
        Domain.Sandbox.Sandbox? sandbox,
        ISandboxExecutor? executor,
        CancellationToken ct) =>
        (await ListAsync(sandbox, executor, ct))
            .Select(a => a.Name).ToList();

    /// <summary>Looks up <paramref name="name"/> (case-insensitive).</summary>
    async Task<AgentDefinition?> FindAsync(
        string name,
        Domain.Sandbox.Sandbox? sandbox,
        ISandboxExecutor? executor,
        CancellationToken ct) =>
        (await ListAsync(sandbox, executor, ct))
            .FirstOrDefault(a => a.Name.Equals(
                name, StringComparison.OrdinalIgnoreCase));
}