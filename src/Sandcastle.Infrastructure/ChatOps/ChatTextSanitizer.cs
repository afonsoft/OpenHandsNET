using System.Text.RegularExpressions;

namespace Sandcastle.Infrastructure.ChatOps;

/// <summary>
/// SPEC-147/151 RF-001 — sanitizer canônico de pings em texto outbound
/// de chat. <see cref="NeutralizeSlackPings"/> cobre os tokens Slack;
/// <see cref="NeutralizeTeamsPings"/> adiciona os pings do Teams
/// (<c>&lt;at&gt;</c>, <c>@team</c>). A neutralização injeta ZWSP
/// (U+200B) depois do <c>@</c>, mantendo o texto legível sem disparar
/// a menção. Idempotente.
/// </summary>
public static partial class ChatTextSanitizer
{
    private const string Zwsp = "\u200B";

    // ---- Slack (SPEC-147) -------------------------------------------------

    /// <summary>Tokens <c>&lt;!channel|here|everyone&gt;</c> (label opcional).</summary>
    [GeneratedRegex(
        @"<!(channel|here|everyone)(\|[^>]*)?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GroupTokens();

    /// <summary>Tokens <c>&lt;!subteam^ID|label&gt;</c>.</summary>
    [GeneratedRegex(
        @"<!subteam\^[A-Za-z0-9]+(\|@?([^>]*))?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubteamTokens();

    /// <summary><c>@channel</c>/<c>@here</c>/<c>@everyone</c> em texto livre.</summary>
    [GeneratedRegex(
        @"(?<![\w.])@(channel|here|everyone)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareGroups();

    // ---- Teams (SPEC-151) -------------------------------------------------

    /// <summary>
    /// Tags de menção do Teams: <c>&lt;at&gt;Name&lt;/at&gt;</c> (com
    /// attrs opcionais, ex.: <c>&lt;at id="…"&gt;</c>) e a forma
    /// HTML-escapada <c>&amp;lt;at&amp;gt;</c>. <c>&lt;at&gt;</c> exige
    /// mention payload para pingar — strip anyway (SPEC-151 §RF-003).
    /// </summary>
    [GeneratedRegex(
        @"(?:<at[^>]*>(.*?)</at>|&lt;at[^&]*&gt;(.*?)&lt;/at&gt;)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        | RegexOptions.Singleline)]
    private static partial Regex TeamsAtTags();

    /// <summary><c>@team</c>/<c>@channel</c>/<c>@everyone</c> bare (Teams).</summary>
    [GeneratedRegex(
        @"(?<![\w.])@(team|channel|everyone)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TeamsBareGroups();

    /// <summary>
    /// Neutraliza pings de grupo Slack. Menções a usuários
    /// (<c>&lt;@U123&gt;</c>) são preservadas.
    /// </summary>
    public static string NeutralizeSlackPings(string? text)
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

    /// <summary>
    /// Neutraliza pings de grupo Teams: tags <c>&lt;at&gt;</c> viram
    /// <c>@​texto</c> inócuo; <c>@team</c>/<c>@channel</c>/<c>@everyone</c>
    /// ganham ZWSP. Aplica também as regras Slack (texto compartilhado
    /// entre sinks passa pelos dois filtros).
    /// </summary>
    public static string NeutralizeTeamsPings(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var result = TeamsAtTags().Replace(text, m =>
            "@" + Zwsp + (m.Groups[1].Success
                ? m.Groups[1].Value : m.Groups[2].Value));
        result = TeamsBareGroups().Replace(result,
            m => "@" + Zwsp + m.Groups[1].Value);
        return NeutralizeSlackPings(result);
    }

    /// <summary>
    /// Despacha pelo provider (<c>"teams"</c> → Teams; demais → Slack).
    /// </summary>
    public static string NeutralizePings(string? text, string? provider) =>
        provider is "teams" or "Teams"
            ? NeutralizeTeamsPings(text)
            : NeutralizeSlackPings(text);
}