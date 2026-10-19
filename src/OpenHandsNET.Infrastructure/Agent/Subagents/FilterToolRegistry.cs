using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.Subagents;

/// <summary>
/// SPEC-28 RF-001/§5: <see cref="IToolRegistry"/> filtered by a preset
/// predicate — the filtered catalog is what the child model sees and what
/// dispatch accepts, so out-of-scope tools are unreachable.
/// </summary>
public sealed class FilterToolRegistry(
    IToolRegistry inner, Func<string, bool> allow) : IToolRegistry
{
    private IReadOnlyList<AgentTool>? _all;

    /// <inheritdoc />
    public IReadOnlyList<AgentTool> All => _all ??= inner.All
        .Where(t => allow(t.Schema.Name)).ToList();

    /// <inheritdoc />
    public AgentTool? Find(string name) =>
        allow(name) ? inner.Find(name) : null;
}