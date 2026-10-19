using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.Context;

/// <summary>
/// Real condenser (SPEC-22 RF-002): threshold evaluation → tool-result
/// pruning → LLM summarization. Never forgets the system prompt, never
/// orphans a tool-call/result pair, and reports the plan so the caller can
/// emit <c>Condensation</c>/<c>CondensationSummaryEvent</c> keeping the
/// event stream a faithful source of truth (replay-consistent: pruned and
/// forgotten ids are persisted).
/// </summary>
public sealed class LlmSummarizingCondenser(
    IContextMeter meter,
    IAgentLlmProvider llm,
    IOptions<AgentLlmOptions> options,
    ILogger<LlmSummarizingCondenser> logger,
    IModelRouter? router = null) : ICondenser
{
    /// <summary>Prompt given to the summarizer role.</summary>
    internal const string SummarizePrompt = """
        You are compressing an agent conversation to fit a context window.
        Summarize the older messages below into a dense handoff the agent can
        continue from. Keep: the user's goal and constraints, decisions made,
        files created/modified (paths), commands run and their outcome,
        pending errors/blockers, and the immediate next step. Write it as
        compact bullet points, no prose intro.
        """;

    /// <inheritdoc />
    public Task<CompactionStart?> EvaluateAsync(
        IReadOnlyList<LlmMessage> messages, string? model, CancellationToken ct)
    {
        var o = options.Value.Compaction;
        if (!o.Enabled)
        {
            return Task.FromResult<CompactionStart?>(null);
        }

        var tokens = meter.EstimateTokens(messages, model);
        var window = meter.ContextWindow(model);
        var threshold = o.ThresholdTokens
            ?? (window is { } w ? (int)(w * o.ThresholdPercent) : (int?)null);
        return Task.FromResult(
            threshold is { } t && tokens >= t
                ? new CompactionStart(CompactionTrigger.Threshold, tokens, window)
                : null);
    }

    /// <inheritdoc />
    public async Task<CondenseResult> CondenseAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<string?> eventIds,
        CompactionTrigger trigger,
        string? model,
        Func<CompactionStart, Task>? onCompacting,
        CancellationToken ct)
    {
        var o = options.Value;
        var co = o.Compaction;
        var tokens = meter.EstimateTokens(messages, model);
        var window = meter.ContextWindow(model);

        if (onCompacting is not null)
        {
            await onCompacting(new CompactionStart(trigger, tokens, window));
        }

        // ---- fase 1: pruning de tool results antigos (barato, sem LLM) ----
        var pruned = new List<string>();
        var working = messages.ToList();
        var ids = eventIds.ToList();
        var toolIdx = working
            .Select((m, i) => (m, i))
            .Where(t => t.m.Role == "tool")
            .Select(t => t.i)
            .ToList();
        foreach (var i in toolIdx.Take(Math.Max(0, toolIdx.Count
            - co.KeepRecentToolResults)))
        {
            var content = working[i].Content;
            if (content is not null && content.Length > co.PruneMaxChars)
            {
                working[i] = working[i] with
                {
                    Content = $"[Output truncated — {content.Length} chars]",
                };
                if (i < ids.Count && ids[i] is { } eid)
                {
                    pruned.Add(eid);
                }
            }
        }

        var afterPrune = meter.EstimateTokens(working, model);
        var threshold = co.ThresholdTokens
            ?? (window is { } w ? (int)(w * co.ThresholdPercent) : (int?)null);
        var needSummary = trigger is not CompactionTrigger.Threshold
            and not CompactionTrigger.MidTurn
            || (threshold is { } t && afterPrune >= t);

        if (!needSummary)
        {
            return new CondenseResult(
                working, ids, [], pruned, null, null, null,
                tokens, afterPrune);
        }

        // ---- fase 2: summarization ----
        var start = working.Count > 0 && working[0].Role == "system" ? 1 : 0;
        var boundary = Math.Max(start, working.Count - co.KeepLastTurns);
        // nunca inicia o tail com tool results órfãos (a call ficaria fora).
        while (boundary < working.Count && working[boundary].Role == "tool")
        {
            boundary++;
        }

        if (boundary - start <= 0)
        {
            return new CondenseResult(
                working, ids, [], pruned, null, null, null,
                tokens, afterPrune);
        }

        try
        {
            var (summary, responseId) = await SummarizeAsync(
                working.GetRange(start, boundary - start), model, o, ct);
            if (string.IsNullOrWhiteSpace(summary))
            {
                return Passthrough();
            }

            var summaryMsg = new LlmMessage(
                "system",
                $"## Earlier context (compacted — {trigger})\n{summary}");
            var resultMsgs = new List<LlmMessage>();
            var resultIds = new List<string?>();
            if (start == 1)
            {
                resultMsgs.Add(working[0]);
                resultIds.Add(ids.Count > 0 ? ids[0] : null);
            }

            resultMsgs.Add(summaryMsg);
            resultIds.Add(null); // preenchido pelo Condensation emitido
            for (var i = boundary; i < working.Count; i++)
            {
                resultMsgs.Add(working[i]);
                resultIds.Add(i < ids.Count ? ids[i] : null);
            }

            // ids podem ser compostos ("id1,id2" — assistant-msg cobrindo
            // várias ActionEvents); split mantém o forget consistente.
            var forgotten = ids.GetRange(
                    start, Math.Min(boundary, ids.Count) - start)
                .OfType<string>()
                .SelectMany(s => s.Split(','))
                .Where(s => s.Length > 0)
                .ToList();
            return new CondenseResult(
                resultMsgs, resultIds, forgotten, pruned, summary, start,
                responseId, tokens,
                meter.EstimateTokens(resultMsgs, model));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // fail-open: histórico podado ainda é devolvido — a run continua.
            logger.LogWarning(ex, "Compaction summarizer failed");
            return Passthrough();
        }

        CondenseResult Passthrough() => new(
            working, ids, [], pruned, null, null, null, tokens, afterPrune);
    }

    private async Task<(string? summary, string responseId)> SummarizeAsync(
        List<LlmMessage> old, string? model, AgentLlmOptions o,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var m in old)
        {
            sb.Append("### ").Append(m.Role).Append('\n');
            var content = m.Content ?? "";
            if (m.ToolCalls is { } calls)
            {
                content += " [tool_calls: " + string.Join(", ",
                    calls.Select(c => c.Name)) + "]";
            }

            var redacted = SecretRedactor.Redact(
                content.Length > 2000 ? content[..2000] + "…" : content);
            sb.Append(redacted).Append("\n\n");
        }

        var text = new StringBuilder();
        var responseId = "";
        // SPEC-23 RF-002: o role Summarizer resolve via router —
        // RoleModels["summarizer"] vence sobre o modelo da conversa.
        var summaryModel = o.Compaction.SummaryModel
            ?? (router is not null
                ? (await router.SelectAsync(Guid.Empty, ModelRole.Summarizer,
                    model, ct)).Model
                : model);
        await foreach (var chunk in llm.ChatStreamAsync(
            [
                new LlmMessage("system", SummarizePrompt),
                LlmMessage.User(sb.ToString()),
            ],
            [],
            new LlmOptions(
                Model: summaryModel,
                MaxTokens: o.Compaction.SummaryMaxTokens),
            ct))
        {
            switch (chunk)
            {
                case TextDelta d:
                    text.Append(d.Text);
                    break;
                case CompletionEnd end:
                    responseId = end.ResponseId;
                    break;
            }
        }

        return (text.ToString().Trim(), responseId);
    }
}