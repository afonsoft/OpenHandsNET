using System.Text.RegularExpressions;

namespace Sandcastle.Infrastructure.ChatOps;

/// <summary>
/// SPEC-147 RF-001: neutraliza pings de grupo em texto outbound do
/// Slack. Resumos gerados pelo LLM podem conter
/// <c>@channel</c>/<c>@here</c>/<c>@everyone</c> ou os tokens
/// <c>&lt;!channel&gt;</c>/<c>&lt;!here&gt;</c>/<c>&lt;!everyone&gt;</c>/
/// <c>&lt;!subteam^…&gt;</c> — postar isso pingaria o canal inteiro.
/// A neutralização injeta um zero-width space (U+200B) depois do
/// <c>@</c>, mantendo o texto legível (<c>@​channel</c>) sem disparar
/// a menção. Menções a usuários (<c>&lt;@U123&gt;</c>) são preservadas.
/// </summary>
public static partial class SlackTextSanitizer
{
    private const string Zwsp = "\u200B";

    /// <summary>
    /// Tokens <c>&lt;!channel|here|everyone&gt;</c> (label opcional).
    /// </summary>
    [GeneratedRegex(
        @"<!(channel|here|everyone)(\|[^>]*)?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GroupTokens();

    /// <summary>
    /// Tokens <c>&lt;!subteam^ID|label&gt;</c> — vira
    /// <c>@​label</c> (ou <c>@​subteam</c> sem label).
    /// </summary>
    [GeneratedRegex(
        @"<!subteam\^[A-Za-z0-9]+(\|@?([^>]*))?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubteamTokens();

    /// <summary>
    /// <c>@channel</c>/<c>@here</c>/<c>@everyone</c> em texto livre. O
    /// lookbehind bloqueia e-mails (<c>ops@channel.io</c>) e handles
    /// colados (<c>x@here</c>).
    /// </summary>
    [GeneratedRegex(
        @"(?<![\w.])@(channel|here|everyone)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareGroups();

    /// <summary>
    /// Substitui todo ping de grupo por uma forma inócua com ZWSP.
    /// Idempotente — rodar duas vezes não muda o resultado.
    /// </summary>
    public static string NeutralizePings(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var result = SubteamTokens().Replace(text, m =>
            m.Groups[2].Success && m.Groups[2].Value.Length > 0
                ? "@" + Zwsp + m.Groups[2].Value
                : "@" + Zwsp + "subteam");
        result = GroupTokens().Replace(result,
            m => "@" + Zwsp + m.Groups[1].Value);
        return BareGroups().Replace(result,
            m => "@" + Zwsp + m.Groups[1].Value);
    }
}