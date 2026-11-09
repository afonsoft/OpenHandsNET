using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Caching.Memory;

using Sandcastle.Application.Agent;
using Sandcastle.Infrastructure.Agent.SystemPrompt;

namespace Sandcastle.Infrastructure.Agent.Security;

/// <summary>
/// SPEC-123 (port OWUIC <c>SmartPermissionClassifier</c>): juiz LLM de
/// permissões opt-in (<c>Agent:SecurityAnalyzer=judge</c>). Prompt
/// embutido <c>prompts/smart-permission-judge.md</c> decide
/// allow/deny/ask por chamada com nonce anti-injeção; always-ask
/// patterns nunca passam pelo juiz; fingerprint SHA-256 deduplica na
/// sessão; falha do provider cai no <see cref="PatternSecurityAnalyzer"/>
/// (fail-safe para padrões locais).
/// </summary>
public sealed class SmartPermissionJudge(
    IAgentLlmProvider llm, IMemoryCache cache) : ISecurityAnalyzer
{
    private static readonly PatternSecurityAnalyzer Fallback = new();
    private static readonly TimeSpan RegexTimeout =
        TimeSpan.FromMilliseconds(500);
    private static readonly string JudgePrompt =
        EmbeddedPrompts.Get("smart-permission-judge");

    /// <summary>Padrões sempre-ask (RF: nunca liberados pelo juiz).</summary>
    public static readonly Regex[] AlwaysAskPatterns =
    [
        new(@"\b(npm|yarn|pnpm|pip|pip3|apt|apt-get|brew|choco)\s+(add|install)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout),
        new(@"\bgit\s+(commit|push|reset|rebase|checkout\s+--|clean\s+-f|restore)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout),
        new(@"\b(rm|rmdir|del)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout),
        new(@"\b(sudo|doas)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout),
        new(@"\bterraform\s+(apply|destroy)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout),
        new(@"\bkubectl\s+delete\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout),
        new(@"\bdocker\s+(rm|rmi)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout),
        new(@"\.env\b|\.pem\b|id_rsa|\.ssh[/\\]|\.aws[/\\]|credentials",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout),
    ];

    /// <inheritdoc />
    public async Task<SecurityRisk> AnalyzeAsync(
        string toolName, JsonElement action, CancellationToken ct)
    {
        var payload = action.GetRawText();
        var haystack = toolName + " " + payload;
        if (AlwaysAskPatterns.Any(p => p.IsMatch(haystack)))
        {
            return SecurityRisk.Medium;
        }

        var fingerprint = $"judge:{toolName}:{Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(payload)))[..16]}";
        if (cache.TryGetValue(fingerprint, out SecurityRisk cached))
        {
            return cached;
        }

        SecurityRisk risk;
        try
        {
            var nonce = Guid.NewGuid().ToString("N")[..12];
            var prompt = JudgePrompt.Length > 0
                ? JudgePrompt
                    + $"\n\n<untrusted_{nonce}>\ntool={toolName}\n"
                    + $"arguments={payload}\n</untrusted_{nonce}>"
                : FallbackInlinePrompt + haystack;
            var text = new StringBuilder();
            await foreach (var chunk in llm.ChatStreamAsync(
                [LlmMessage.User(prompt)], [],
                new LlmOptions(MaxTokens: 128, Temperature: 0), ct))
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

        cache.Set(fingerprint, risk, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
        });
        return risk;
    }

    // Mapeia verdict allow/deny/ask → SecurityRisk (low/medium/high).
    private static SecurityRisk Parse(string text)
    {
        try
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(text[start..(end + 1)]);
                var decision = doc.RootElement
                    .GetProperty("decision").GetString();
                return decision switch
                {
                    "allow" => SecurityRisk.Low,
                    "deny" => SecurityRisk.High,
                    _ => SecurityRisk.Medium,
                };
            }
        }
        catch (JsonException)
        { // cai no fail-safe abaixo
        }
        return SecurityRisk.Medium;
    }

    private const string FallbackInlinePrompt =
        "You are a security judge for an AI agent's permission system. "
        + "Decide allow|deny|ask for ONE tool action and answer with a "
        + "JSON object {\"decision\":\"allow|deny|ask\",\"reason\":\"...\"}.\n";
}