using System.Text;

using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Infrastructure.Agent.SystemPrompt;

namespace Sandcastle.Infrastructure.Agent;

/// <summary>
/// SPEC-125: consulta LLM one-shot com <c>prompts/advisor-system.md</c>
/// como system prompt — sem tools, temperatura baixa, timeout ≤ 30s.
/// Fail-open: qualquer falha do provider devolve null (o run continua).
/// </summary>
public sealed class AdvisorService(
    IAgentLlmProvider llm, IOptions<AgentLlmOptions> options)
    : IAdvisorService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly string SystemPrompt =
        EmbeddedPrompts.Get("advisor-system");

    /// <inheritdoc />
    public bool Enabled =>
        !string.IsNullOrWhiteSpace(options.Value.AdvisorModel);

    /// <inheritdoc />
    public async Task<AdvisorVerdict?> ConsultAsync(
        string context, string question, CancellationToken ct)
    {
        if (!Enabled)
        {
            return null;
        }

        var model = options.Value.AdvisorModel!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var linked = CancellationTokenSource
                .CreateLinkedTokenSource(ct);
            linked.CancelAfter(Timeout);

            var user = new StringBuilder()
                .Append("## Context\n").Append(context)
                .Append("\n\n## Question\n").Append(question)
                .ToString();
            var text = new StringBuilder();
            await foreach (var chunk in llm.ChatStreamAsync(
                [new LlmMessage("system", SystemPrompt),
                    LlmMessage.User(user)],
                [],
                new LlmOptions(
                    Model: model, Temperature: 0.2, MaxTokens: 256),
                linked.Token))
            {
                if (chunk is TextDelta delta)
                {
                    text.Append(delta.Text);
                }
            }

            var answer = text.ToString().Trim();
            return answer.Length == 0
                ? null
                : new AdvisorVerdict(answer, model, sw.ElapsedMilliseconds);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null; // fail-open — advisor nunca quebra o run.
        }
    }
}