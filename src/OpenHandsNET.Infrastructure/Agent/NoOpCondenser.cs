using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent;

/// <summary>No-op condenser (SPEC-09 MVP / tests) — history passes through.</summary>
public sealed class NoOpCondenser : ICondenser
{
    /// <inheritdoc />
    public Task<CompactionStart?> EvaluateAsync(
        IReadOnlyList<LlmMessage> messages, string? model, CancellationToken ct) =>
        Task.FromResult<CompactionStart?>(null);

    /// <inheritdoc />
    public Task<CondenseResult> CondenseAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<string?> eventIds,
        CompactionTrigger trigger,
        string? model,
        Func<CompactionStart, Task>? onCompacting,
        CancellationToken ct) =>
        Task.FromResult(new CondenseResult(
            messages, eventIds, [], [], null, null, null, 0, 0));
}