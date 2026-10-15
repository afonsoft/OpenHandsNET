using System.Collections.Concurrent;
using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.Subagents;

/// <summary>
/// SPEC-28 RF-003/§4: in-memory per-conversation registry of subagent runs.
/// Process-scoped — on restart children are gone; stale <c>Running</c>
/// entries report <c>Aborted</c> via <see cref="AbortAll"/>.
/// </summary>
public sealed class SubagentHost : ISubagentHost
{
    private readonly ConcurrentDictionary<Guid,
        ConcurrentDictionary<string, SubagentRun>> _runs = new();

    /// <inheritdoc />
    public bool TryStart(
        Guid conversationId, SubagentRun run, int maxConcurrent)
    {
        var conv = _runs.GetOrAdd(conversationId,
            _ => new ConcurrentDictionary<string, SubagentRun>());
        var active = conv.Values.Count(r =>
            r.Info.Status == SubagentStatus.Running);
        if (active >= maxConcurrent)
        {
            return false;
        }

        return conv.TryAdd(run.Info.TaskId, run);
    }

    /// <inheritdoc />
    public void Finish(Guid conversationId, string taskId,
        SubagentStatus status, string? result = null, string? error = null)
    {
        if (_runs.TryGetValue(conversationId, out var conv)
            && conv.TryGetValue(taskId, out var run))
        {
            run.Info = run.Info with
            {
                Status = status, Result = result, Error = error,
                EndedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    /// <inheritdoc />
    public SubagentRun? Get(Guid conversationId, string taskId) =>
        _runs.TryGetValue(conversationId, out var conv)
            && conv.TryGetValue(taskId, out var run) ? run : null;

    /// <inheritdoc />
    public IReadOnlyList<SubagentRunInfo> List(Guid conversationId) =>
        _runs.TryGetValue(conversationId, out var conv)
            ? conv.Values.Select(r => r.Info)
                .OrderByDescending(i => i.StartedAt).ToList()
            : [];

    /// <inheritdoc />
    public void AbortAll(Guid conversationId)
    {
        if (!_runs.TryGetValue(conversationId, out var conv))
        {
            return;
        }

        foreach (var run in conv.Values)
        {
            if (run.Info.Status == SubagentStatus.Running)
            {
                run.Cts?.Cancel();
            }
        }
    }
}
