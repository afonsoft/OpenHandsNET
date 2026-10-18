namespace OpenHandsNET.Application.Common.Models;

/// <summary>
/// Represents the result of an output compaction operation.
/// </summary>
/// <param name="IsCompacted">True if the output was truncated/summarized.</param>
/// <param name="CompactContent">The condensed text content intended for the LLM.</param>
/// <param name="ArtifactId">A reference to the full log in storage (if compacted).</param>
public record CompactedOutput(
    bool IsCompacted,
    string CompactContent,
    string? ArtifactId = null
);
