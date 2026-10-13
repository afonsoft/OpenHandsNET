using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent;

/// <summary>No-op condenser (SPEC-09 MVP) — history passes through untouched.</summary>
public sealed class NoOpCondenser : ICondenser
{
    /// <inheritdoc />
    public Task<IReadOnlyList<LlmMessage>> CondenseAsync(
        IReadOnlyList<LlmMessage> messages, CancellationToken ct) =>
        Task.FromResult(messages);
}
