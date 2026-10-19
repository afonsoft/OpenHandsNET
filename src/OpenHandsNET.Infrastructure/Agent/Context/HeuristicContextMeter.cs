using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.Context;

/// <summary>
/// Heuristic token meter (SPEC-22 RF-001): chars/4 + per-message overhead,
/// calibrated online by the observed <c>prompt_tokens</c> of the previous
/// completion. Windows come from <c>AgentLlm:ContextWindowOverrides</c> then
/// <c>AgentLlm:DefaultContextWindow</c> (SPEC-23 plugs catalog discovery in).
/// </summary>
public sealed class HeuristicContextMeter(
    IOptions<AgentLlmOptions> options, IModelCatalog? catalog = null)
    : IContextMeter
{
    private const int PerMessageOverhead = 4;

    private double _calibration = 1.0;

    /// <inheritdoc />
    public int EstimateTokens(IReadOnlyList<LlmMessage> messages, string? model)
    {
        long chars = 0;
        foreach (var m in messages)
        {
            chars += PerMessageOverhead * 4; // overhead counted as chars
            chars += m.Role.Length;
            if (m.Content is { } c)
            {
                chars += c.Length;
            }

            if (m.ToolCalls is { } calls)
            {
                foreach (var call in calls)
                {
                    chars += call.Id.Length + call.Name.Length
                        + call.ArgumentsJson.Length;
                }
            }

            if (m.ToolCallId is { } tid)
            {
                chars += tid.Length;
            }
        }

        return (int)Math.Max(1, Math.Round(chars / 4.0 * _calibration));
    }

    /// <inheritdoc />
    public int? ContextWindow(string? model)
    {
        var o = options.Value;
        if (model is not null)
        {
            if (o.ContextWindowOverrides.TryGetValue(model, out var win))
            {
                return win;
            }

            // SPEC-23 RF-001: janela descoberta pelo catálogo OmniRoute.
            if (catalog?.TryGetCached(model)?.ContextWindow is { } cw)
            {
                return cw;
            }
        }

        return o.DefaultContextWindow;
    }

    /// <inheritdoc />
    public void ObserveUsage(int promptTokens, int estimatedTokens)
    {
        if (promptTokens <= 0 || estimatedTokens <= 0)
        {
            return;
        }

        // Fator de correção com clamp — um provider com tokenizer generoso não
        // pode explodir a estimativa (e vice-versa).
        var ratio = (double)promptTokens / estimatedTokens;
        _calibration = Math.Clamp(_calibration * ratio, 0.7, 1.5);
    }
}