using System.Text.RegularExpressions;

namespace OpenHandsNET.Infrastructure.Security;

/// <summary>
/// SPEC-91 — envelope <c>&lt;untrusted_input&gt;</c> para conteúdo externo
/// que entra em prompts (webhooks SCM, tickets, chatops). Atributos com
/// HTML-escape; tag de fechamento neutralizada no valor; caps de 4KB por
/// campo e 8KB agregado via <see cref="Envelope"/>.
/// Port do <c>WebhookPromptTemplate</c> do open-webui.
/// </summary>
public static partial class UntrustedInput
{
    /// <summary>Tamanho máximo de um campo envelopado.</summary>
    public const int MaxFieldChars = 4096;

    /// <summary>Tamanho máximo agregado de envelopes num prompt.</summary>
    public const int MaxTotalChars = 8192;

    /// <summary>Marcador de truncamento dentro do envelope.</summary>
    public const string TruncatedMark = "[…truncated]";

    /// <summary>
    /// Envelopa <paramref name="value"/> como dado externo:
    /// <c>&lt;untrusted_input source="src" field="f"&gt;valor&lt;/untrusted_input&gt;</c>.
    /// <paramref name="field"/> nulo omite o atributo.
    /// </summary>
    public static string Wrap(string? value, string source, string? field = null)
        => Wrap(value, source, field, MaxFieldChars);

    internal static string Wrap(
        string? value, string source, string? field, int fieldCap)
    {
        var body = Neutralize(Cap(value ?? string.Empty, fieldCap));
        var attrs = $"source=\"{Attr(source)}\"";
        if (field is not null)
        {
            attrs += $" field=\"{Attr(field)}\"";
        }
        return $"<untrusted_input {attrs}>{body}</untrusted_input>";
    }

    /// <summary>Trunca <paramref name="value"/> em
    /// <paramref name="maxChars"/> com marcador; nunca corta um
    /// surrogate pair no meio.</summary>
    public static string Cap(string value, int maxChars)
    {
        if (value.Length <= maxChars)
        {
            return value;
        }
        var end = maxChars;
        if (end > 0 && char.IsHighSurrogate(value[end - 1]))
        {
            end--;
        }
        return string.Concat(value.AsSpan(0, end), TruncatedMark);
    }

    private static string Attr(string v) => v
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");

    private static string Neutralize(string v) =>
        CloseTagRegex().Replace(v, "&lt;/untrusted_input");

    [GeneratedRegex(@"<\s*/\s*untrusted_input", RegexOptions.IgnoreCase)]
    private static partial Regex CloseTagRegex();

    /// <summary>
    /// Budget agregado de envelopes num prompt (RF-003): cada
    /// <see cref="Wrap"/> consome do total (default 8KB) — campos
    /// excedentes truncam com marcador.
    /// </summary>
    public sealed class Envelope
    {
        private int remaining;

        /// <summary>Cria budget agregado (default
        /// <see cref="MaxTotalChars"/>).</summary>
        public Envelope(int maxTotalChars = MaxTotalChars) =>
            remaining = maxTotalChars;

        /// <summary>Envelopa consumindo do budget agregado — o cap
        /// por campo é <c>min(4KB, budget restante)</c>.</summary>
        public string Wrap(string? value, string source, string? field = null)
        {
            var cap = Math.Min(MaxFieldChars, Math.Max(0, remaining));
            var s = UntrustedInput.Wrap(value, source, field, cap);
            remaining -= s.Length;
            return s;
        }
    }
}