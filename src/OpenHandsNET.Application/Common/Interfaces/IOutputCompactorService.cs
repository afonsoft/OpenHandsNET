namespace OpenHandsNET.Application.Common.Interfaces;

using OpenHandsNET.Application.Common.Models;

/// <summary>
/// Service responsible for condensing large tool outputs to prevent LLM context overflow.
/// </summary>
public interface IOutputCompactorService
{
    /// <summary>
    /// Compacts a large text output, returning a summary and an artifact reference if necessary.
    /// </summary>
    /// <param name="originalOutput">The full raw output from the tool/command.</param>
    /// <param name="context">Contextual info (e.g., tool name) for better summarization.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A compacted version of the output.</returns>
    Task<CompactedOutput> CompactAsync(string originalOutput, string context, CancellationToken ct = default);
}