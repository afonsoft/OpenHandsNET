using System.Text.RegularExpressions;

namespace OpenHandsNET.Infrastructure.Indexing;

/// <summary>
/// SPEC-60 RF-001: chunking "AST-simplificada" — declarações por regex
/// por linguagem, com sliding-window como fallback. Globs no estilo
/// `.openhands/indexignore` são expandidos para regex.
/// </summary>
public static class CodeChunker
{
    public const int SlidingWindowLines = 60;
    public const int SlidingWindowOverlap = 15;

    private static readonly (string[] Exts, string Pattern)[] DeclPatterns =
    [
        ([".cs", ".fs", ".vb"],
            @"^\s*(?:public|private|internal|protected|static|sealed|abstract|partial|readonly|async|virtual|override|new|extern|unsafe|file|required|record|class|struct|interface|enum|delegate|namespace|void|Task|ValueTask|I[A-Z][\w<>?,\[\] ]*|[A-Z][\w<>?,\[\] ]*|bool|int|string|long|double|float|decimal|var|object|dynamic|char|byte|short|uint|ulong|ushort|sbyte|nint|nuint)\s+[\w<>?,\[\] ()=>:;]*\s*(?:class|struct|interface|enum|record|delegate|\w+\s*\()"),
        ([".py"],
            @"^\s*(?:async\s+def|def|class)\s+\w+"),
        ([".js", ".ts", ".jsx", ".tsx"],
            @"^\s*(?:export\s+)?(?:async\s+)?(?:function|class|const\s+\w+\s*=\s*(?:async\s*)?(?:\(|function)|interface|type|enum)\b"),
        ([".go"], @"^\s*(?:func|type)\s+"),
        ([".rs"], @"^\s*(?:pub\s+)?(?:fn|struct|enum|impl|trait|mod)\s+"),
        ([".java", ".kt", ".scala"],
            @"^\s*(?:public|private|protected|static|final|abstract|suspend|fun|class|interface|object|enum|sealed|data|internal)\s+.*[\w(]"),
        ([".rb"], @"^\s*(?:def|class|module)\s+"),
        ([".cpp", ".cc", ".cxx", ".h", ".hpp", ".c"],
            @"^\s*(?:template|class|struct|enum|namespace|void|int|bool|auto|static|inline|virtual|const|unsigned|signed|long|short|char|float|double|std::\w+)\b.*[\w:(]"),
        ([".sql"], @"^\s*(?:CREATE|create)\s+(?:TABLE|VIEW|INDEX|FUNCTION|PROCEDURE|TRIGGER)\b"),
        ([".sh", ".bash"], @"^\s*(?:function\s+)?\w+\s*\(\)\s*\{"),
    ];

    /// <summary>Extensões de texto indexáveis.</summary>
    public static readonly HashSet<string> IndexableExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".py", ".js", ".ts", ".jsx", ".tsx",
        ".go", ".rs", ".java", ".kt", ".scala", ".rb", ".cpp", ".cc",
        ".cxx", ".h", ".hpp", ".c", ".sql", ".sh", ".bash",
        ".md", ".txt", ".json", ".yaml", ".yml", ".toml", ".xml",
        ".razor", ".cshtml", ".html", ".css", ".proto", ".tf",
        ".dockerfile", ".gradle", ".props", ".csproj", ".sln",
        ".slnx", ".ini", ".cfg", ".env", ".csv",
    };

    /// <summary>
    /// Converte glob estilo indexignore em regex (âncora total).
    /// Suporta `**`, `*`, `?` e comentários `#`.
    /// </summary>
    public static Regex? GlobToRegex(string glob)
    {
        var g = glob.Trim();
        if (g.Length == 0 || g.StartsWith('#'))
            return null;
        // Sem "/" no glob → casa em qualquer nível (semântica .gitignore).
        var sb = new System.Text.StringBuilder(
            g.Contains('/') ? "^" : "^(?:.*/)?");
        for (var i = 0; i < g.Length; i++)
        {
            var c = g[i];
            if (c == '*')
            {
                if (i + 1 < g.Length && g[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else if (c == '/')
            {
                sb.Append('/');
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>
    /// Chunking: tenta fronteiras de declaração (cada decl inicia um
    /// bloco até a próxima, com teto de <see cref="SlidingWindowLines"/>);
    /// linguagem sem padrão → sliding window puro. Retorna unidades com
    /// (startLine, endLine, kind, text).
    /// </summary>
    public static IReadOnlyList<(int Start, int End, string Kind, string Text)>
        Chunk(string path, string[] lines)
    {
        var decl = DeclPatternFor(path);
        if (decl is null)
            return Sliding(lines).ToList();

        var result = new List<(int, int, string, string)>();
        var blockStart = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var isDecl = decl.IsMatch(lines[i]);
            if (isDecl || i == lines.Length - 1)
            {
                if (blockStart >= 0)
                {
                    var end = isDecl ? i - 1 : i;
                    if (end >= blockStart)
                    {
                        if (end - blockStart + 1 > SlidingWindowLines)
                        {
                            foreach (var u in SlidingRange(
                                lines, blockStart, end))
                            {
                                result.Add(u);
                            }
                        }
                        else
                        {
                            result.Add((blockStart, end, "symbol",
                                string.Join('\n',
                                    lines[blockStart..(end + 1)])));
                        }
                    }
                }
                blockStart = isDecl ? i : -1;
            }
        }
        if (blockStart >= 0 && blockStart < lines.Length)
        {
            foreach (var u in SlidingRange(
                lines, blockStart, lines.Length - 1))
            {
                result.Add(u);
            }
        }
        return result;
    }

    private static Regex? DeclPatternFor(string path)
    {
        var ext = Path.GetExtension(path);
        var name = Path.GetFileName(path);
        if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
            return null;
        foreach (var (exts, pattern) in DeclPatterns)
        {
            if (exts.Contains(ext, StringComparer.OrdinalIgnoreCase))
                return new Regex(pattern, RegexOptions.Compiled);
        }
        return null;
    }

    private static IEnumerable<(int, int, string, string)> Sliding(
        string[] lines) => SlidingRange(lines, 0, lines.Length - 1).ToList();

    private static IEnumerable<(int, int, string, string)> SlidingRange(
        string[] lines, int from, int to)
    {
        var i = from;
        while (i <= to)
        {
            var end = Math.Min(i + SlidingWindowLines - 1, to);
            yield return (i, end, "chunk",
                string.Join('\n', lines[i..(end + 1)]));
            if (end == to)
                break;
            i = end - SlidingWindowOverlap + 1;
        }
    }
}
