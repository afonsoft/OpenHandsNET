using System.Text.RegularExpressions;

using OpenHandsNET.Application.Security;

namespace OpenHandsNET.Infrastructure.Security;

/// <summary>
/// SPEC-59 RF-002: screening por regexes/heurísticas — sempre ativo
/// (fail-safe, &lt;1ms). Categorias: prompt_injection, exfiltration,
/// control_markup.
/// </summary>
public sealed partial class PatternInputGuard : IInputGuard
{
    private const string PromptInjection = "prompt_injection";
    private sealed record Rule(Regex Rx, string Category, bool Block);

    private static readonly Rule[] Rules =
    [
        // injection clássica
        new(IgnorePreviousRx(), PromptInjection, true),
        new(SystemPromptRx(), PromptInjection, true),
        new(YouAreNowRx(), PromptInjection, true),
        new(DisregardRx(), PromptInjection, true),
        // exfiltração de segredos
        new(RevealSecretRx(), "exfiltration", true),
        new(ExfilNetRx(), "exfiltration", true),
        // markup de controle de LLM
        new(ControlMarkupRx(), "control_markup", true),
        // heurísticas mais fracas → warn
        new(EncodedPayloadRx(), "control_markup", false),
        new(ActAsRx(), PromptInjection, false),
    ];

    /// <inheritdoc />
    public Task<InputGuardResult> ScoreAsync(
        string text, string channel, Guid? orgId, CancellationToken ct)
    {
        var categories = new List<string>();
        var block = false;
        foreach (var (rx, cat, b) in Rules)
        {
            if (rx.IsMatch(text))
            {
                categories.Add(cat);
                block |= b;
            }
        }

        var verdict = (block, categories.Count > 0) switch
        {
            (true, _) => InputGuardVerdict.Block,
            (false, true) => InputGuardVerdict.Warn,
            _ => InputGuardVerdict.Allow,
        };
        // Motivo sem o conteúdo — só categorias + hash curto.
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(text)))[..12];
        return Task.FromResult(new InputGuardResult(
            verdict, categories.Distinct().ToList(),
            $"{verdict}:{string.Join(',', categories.Distinct())}:sha256:{hash}"));
    }

    [GeneratedRegex(
        "ignore (all |any |the )?(previous|prior|above|earlier) instructions",
        RegexOptions.IgnoreCase)]
    private static partial Regex IgnorePreviousRx();

    [GeneratedRegex(
        "(reveal|print|output|show|tell me|repeat).{0,40}(system prompt|"
        + "your (prompt|instructions|rules))",
        RegexOptions.IgnoreCase)]
    private static partial Regex SystemPromptRx();

    [GeneratedRegex(
        "you are now (a |an |in )?(a )?(new |different )?",
        RegexOptions.IgnoreCase)]
    private static partial Regex YouAreNowRx();

    [GeneratedRegex("disregard .{0,30}(instructions|rules|guidelines)",
        RegexOptions.IgnoreCase)]
    private static partial Regex DisregardRx();

    [GeneratedRegex(
        "(api[_ -]?key|secret|password|token|credential)s?.{0,30}"
        + "(to|send|post|exfiltrat|print|reveal|base64|encode)",
        RegexOptions.IgnoreCase)]
    private static partial Regex RevealSecretRx();

    [GeneratedRegex(
        "(curl|wget|fetch|nslookup|dig).{0,60}"
        + "(--data|-d |\\$\\(|`.*`|https?://[^/]*\\$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExfilNetRx();

    [GeneratedRegex(
        "(<\\|im_start|<\\|im_end|<<SYS>>|</?\\[?INST\\]?>|"
        + "<\\|endoftext\\|>)")]
    private static partial Regex ControlMarkupRx();

    [GeneratedRegex("(^|\\s)[A-Za-z0-9+/]{400,}={0,2}(\\s|$)")]
    private static partial Regex EncodedPayloadRx();

    [GeneratedRegex("(act|pretend|behave|roleplay) as (if |a |an )?",
        RegexOptions.IgnoreCase)]
    private static partial Regex ActAsRx();
}