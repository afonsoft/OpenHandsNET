using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.SystemPrompt;

/// <summary>
/// Ordered, failure-tolerant composer (SPEC-90 RF-004): runs
/// <see cref="IAgentPromptContributor"/>s in ascending
/// <see cref="IAgentPromptContributor.Order"/>, concatenates non-empty
/// sections with a blank line and tolerates a throwing contributor —
/// the failed section is omitted, a warning is logged and the rest of
/// the prompt still builds.
/// </summary>
public sealed class SystemPromptComposer(
    IEnumerable<IAgentPromptContributor> contributors,
    ILogger<SystemPromptComposer> logger)
{
    /// <summary>Builds the full system prompt for <paramref name="ctx"/>.</summary>
    public async Task<string> ComposeAsync(AgentPromptContext ctx)
    {
        var sections = new List<string>();
        foreach (var contributor in contributors.OrderBy(c => c.Order))
        {
            IReadOnlyList<string> contributed;
            try
            {
                contributed = await contributor.ContributeAsync(ctx);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Prompt contributor {Contributor} failed — section omitted",
                    contributor.GetType().Name);
                continue;
            }

            sections.AddRange(contributed
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.TrimEnd()));
        }

        return sections.Count == 0
            ? string.Empty
            : string.Join("\n\n", sections) + "\n";
    }
}