using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.Security;

/// <summary>
/// SPEC-33 RF-001 (opt-in): LLM-backed analyzer — classifies the tool-call
/// payload as <c>high|medium|low</c> via one small completion; results are
/// LRU-cached by sha256(tool+args) so repeat calls are free. Falls back to
/// <see cref="PatternSecurityAnalyzer"/> on provider failure (fail-open →
/// whatever patterns say).
/// </summary>
public sealed class LlmSecurityAnalyzer(
    IAgentLlmProvider llm, IMemoryCache cache) : ISecurityAnalyzer
{
    private static readonly PatternSecurityAnalyzer Fallback = new();

    private const string Prompt = """
        You are a security analyzer for an agentic coding tool. Classify the
        risk of the following tool call payload for an autonomous agent
        running inside a dev sandbox. Answer with EXACTLY one word:
        high — destructive/irreversible outside the workspace (rm -rf of
          system paths, dd to block devices, credential exfiltration,
          persistence mechanisms, supply-chain sinks like curl|sh).
        medium — elevated but local (sudo, process kills, downloads+exec).
        low — routine workspace operations.
        Payload:
        """;

    /// <inheritdoc />
    public async Task<SecurityRisk> AnalyzeAsync(
        string toolName, JsonElement action, CancellationToken ct)
    {
        var payload = action.GetRawText();
        var key = $"sec:{toolName}:{Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..16]}";
        if (cache.TryGetValue(key, out SecurityRisk cached))
        {
            return cached;
        }

        SecurityRisk risk;
        try
        {
            var text = new StringBuilder();
            await foreach (var chunk in llm.ChatStreamAsync(
                [LlmMessage.User(Prompt + payload)], [],
                new LlmOptions(MaxTokens: 8, Temperature: 0), ct))
            {
                if (chunk is TextDelta delta)
                {
                    text.Append(delta.Text);
                }
            }

            risk = Parse(text.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            risk = await Fallback.AnalyzeAsync(toolName, action, ct);
        }

        cache.Set(key, risk, new MemoryCacheEntryOptions
        {
            Size = 1, AbsoluteExpirationRelativeToNow =
                TimeSpan.FromMinutes(30),
        });
        return risk;
    }

    private static SecurityRisk Parse(string text) =>
        text.Trim().ToLowerInvariant() switch
        {
            var s when s.StartsWith("high", StringComparison.Ordinal) =>
                SecurityRisk.High,
            var s when s.StartsWith("medium", StringComparison.Ordinal) =>
                SecurityRisk.Medium,
            _ => SecurityRisk.Low,
        };
}
