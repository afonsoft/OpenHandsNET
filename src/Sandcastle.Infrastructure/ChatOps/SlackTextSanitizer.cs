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
/// SPEC-151: implementação movida p/ <see cref="ChatTextSanitizer"/> —
/// este wrapper preserva o nome público histórico.
/// </summary>
public static class SlackTextSanitizer
{
    /// <summary>
    /// Substitui todo ping de grupo por uma forma inócua com ZWSP.
    /// Idempotente — rodar duas vezes não muda o resultado.
    /// </summary>
    public static string NeutralizePings(string? text) =>
        ChatTextSanitizer.NeutralizeSlackPings(text);
}