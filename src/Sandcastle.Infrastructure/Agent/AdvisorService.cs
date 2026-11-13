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
    IAgentLlmProvider llm, IOptions<AgentLlmOptions> options,
    IModelRouter? router = null)
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
        string context, string question, CancellationToken ct,
        LlmRouteContext? routeCtx = null)
    {
        if (!Enabled)
        {
            return null;
        }

        // SPEC-149: advisor é a role critic — rules da org vencem quando
        // configuradas; sem rules o AdvisorModel passa (comportamento
        // atual — PreferredModel preserva o pin legado). Rules ativas
        // sem candidato elegível ⇒ null respeita a policy (advisor é
        // fail-open, nunca um modelo arbitrário fora da allowlist).
        var model = options.Value.AdvisorModel!;
        if (router is not null)
        {
            var sel = await router.ResolveAsync(LlmRole.Critic,
                (routeCtx ?? new LlmRouteContext()) with
                {
                    PreferredModel = options.Value.AdvisorModel,
                }, ct);
            if (sel is null)
            {
                return null;
            }
            model = sel.Model;
        }
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