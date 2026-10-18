using OpenHandsNET.Application.Common.Interfaces;
using OpenHandsNET.Application.Common.Models;
using Microsoft.Extensions.Logging;

namespace OpenHandsNET.Infrastructure.Agent;

/// <summary>
/// Default implementation of <see cref="IOutputCompactorService" /> that uses
/// head/tail truncation for large outputs.
/// </summary>
public class OutputCompactorService : IOutputCompactorService
{
    private readonly ILogger<OutputCompactorService> _logger;
    private const int MaxInlineChars = 4000;
    private const int HeadLines = 15;
    private const int TailLines = 15;

    public OutputCompactorService(ILogger<OutputCompactorService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<CompactedOutput> CompactAsync(string originalOutput, string context, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(originalOutput))
        {
            return Task.FromResult(new CompactedOutput(false, string.Empty));
        }

        if (originalOutput.Length <= MaxInlineChars)
        {
            return Task.FromResult(new CompactedOutput(false, originalOutput));
        }

        _logger.LogInformation("Compacting large output for context: {Context}. Original length: {Length}", context, originalOutput.Length);

        // In a real enterprise scenario, we would save originalOutput to IArtifactStorage here
        // and return the artifact ID. For Phase 1, we'll focus on the content formatting.
        string artifactId = $"artifact://outputs/{Guid.NewGuid():N}.log";

        var lines = originalOutput.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        
        var head = lines.Take(HeadLines);
        var tail = lines.Skip(Math.Max(HeadLines, lines.Length - TailLines));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[OUTPUT TRUNCATED - Full log stored at {artifactId}]");
        sb.AppendLine("--- HEAD ---");
        foreach (var line in head) sb.AppendLine(line);
        
        sb.AppendLine("...");
        
        sb.AppendLine("--- TAIL ---");
        foreach (var line in tail) sb.AppendLine(line);

        return Task.FromResult(new CompactedOutput(true, sb.ToString(), artifactId));
    }
}
