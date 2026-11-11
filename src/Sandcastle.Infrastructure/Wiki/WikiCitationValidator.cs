using System.Text.RegularExpressions;

using Sandcastle.Application.Scm;

namespace Sandcastle.Infrastructure.Wiki;

/// <summary>
/// Citação <c>path:Lx</c> ou <c>path:Lx-Ly</c> extraída do markdown da wiki.
/// </summary>
public sealed record WikiCitation(string Path, int StartLine, int? EndLine);

/// <summary>
/// Validação de citações da wiki (SPEC-145 guardrail): todo
/// <c>path:Lx(-Ly)</c> precisa existir no índice (e a linha no arquivo).
/// Citações válidas são reescritas como links
/// <c>[path:Lx-Ly](permalink)</c>; uma inválida marca a página stale —
/// a wiki nunca publica caminhos inventados.
/// </summary>
public static class WikiCitationValidator
{
    // path:Lx | path:Lx-Ly — path com letra/dígito/_/.//- e extensão ou
    // separador (evita casar "nota: lorem" e URLs).
    private static readonly Regex CitationPattern = new(
        @"(?<path>(?:[A-Za-z0-9_.\-]+/)*[A-Za-z0-9_.\-]+\.[A-Za-z0-9]{1,10}):L(?<s>\d+)(?:-L?(?<e>\d+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    /// <summary>Citações encontradas no conteúdo.</summary>
    public static IReadOnlyList<WikiCitation> Extract(string contentMd)
    {
        var result = new List<WikiCitation>();
        foreach (Match m in CitationPattern.Matches(contentMd))
        {
            var path = m.Groups["path"].Value.TrimStart('.', '/');
            if (path.Length == 0 || path.StartsWith("http",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var s = int.Parse(m.Groups["s"].Value);
            int? e = m.Groups["e"].Success
                ? int.Parse(m.Groups["e"].Value) : null;
            result.Add(new WikiCitation(path, s, e));
        }
        return result;
    }

    /// <summary>
    /// Reescreve cada citação <c>path:Lx(-Ly)</c> como link
    /// <c>[path:Lx-Ly](permalink)</c> quando o path existe em
    /// <paramref name="indexedPaths"/> (case-insensitive) e a linha está
    /// dentro do arquivo (<paramref name="lineCountOf"/> — null pula a
    /// checagem de linha). Retorna o conteúdo reescrito + as citações
    /// inválidas encontradas.
    /// </summary>
    public static (string Content, IReadOnlyList<WikiCitation> Invalid)
        ValidateAndRewrite(
            string contentMd,
            IReadOnlySet<string> indexedPaths,
            Func<string, int?> lineCountOf,
            ScmConnection? connection,
            string repository,
            string? commitSha)
    {
        var invalid = new List<WikiCitation>();
        var seen = new HashSet<WikiCitation>();
        var rewritten = CitationPattern.Replace(contentMd, m =>
        {
            var path = m.Groups["path"].Value.TrimStart('.', '/');
            var s = int.Parse(m.Groups["s"].Value);
            int? e = m.Groups["e"].Success
                ? int.Parse(m.Groups["e"].Value) : null;
            var citation = new WikiCitation(path, s, e);

            var indexed = indexedPaths.FirstOrDefault(p =>
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            var lineCount = indexed is not null ? lineCountOf(indexed) : null;
            var lineOk = indexed is not null
                && (lineCount is null || s <= lineCount);
            if (indexed is null || !lineOk)
            {
                if (seen.Add(citation))
                {
                    invalid.Add(citation);
                }
                return m.Value; // deixa o token como texto — nunca inventa link.
            }

            var label = e is { } ee && ee > s
                ? $"{indexed}:L{s}-L{ee}" : $"{indexed}:L{s}";
            var url = WikiPermalink.Build(
                connection, repository, commitSha, indexed, s, e);
            return url is null ? $"`{label}`" : $"[{label}]({url})";
        });
        return (rewritten, invalid);
    }
}