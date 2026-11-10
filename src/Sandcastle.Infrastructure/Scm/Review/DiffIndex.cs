using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Sandcastle.Application.Scm;

namespace Sandcastle.Infrastructure.Scm.Review;

/// <summary>
/// SPEC-140 RF-002/RF-003 — índice do diff de um PR: para cada arquivo, o
/// conjunto de linhas do lado NOVO (RIGHT) que podem ancorar comentários,
/// mais helpers de seleção (caps + heurística não-generated) e o hash do
/// diff p/ dedup de re-review.
/// </summary>
public sealed partial class DiffIndex
{
    private readonly Dictionary<string, FileLines> _files =
        new(StringComparer.Ordinal);

    /// <summary>Paths presentes no diff.</summary>
    public IReadOnlyCollection<string> Paths => _files.Keys;

    /// <summary>Linhas válidas (lado novo) de <paramref name="path"/>.</summary>
    public IReadOnlySet<int> NewLinesOf(string path) =>
        _files.TryGetValue(path, out var f)
            ? f.NewLines
            : new HashSet<int>();

    /// <summary>
    /// Linha é âncora válida: <paramref name="path"/> no diff e
    /// <paramref name="line"/> presente no lado novo do patch.
    /// </summary>
    public bool IsAnchorable(string path, int line) =>
        _files.TryGetValue(path, out var f) && f.NewLines.Contains(line);

    /// <summary>Parse de <paramref name="patch"/> unificado de um arquivo.</summary>
    public static FileLines ParsePatch(string? patch)
    {
        var lines = new FileLines();
        if (string.IsNullOrEmpty(patch))
        {
            return lines;
        }

        var newLine = 0;
        foreach (var raw in patch.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var m = HunkHeader().Match(line);
                if (m.Success)
                {
                    newLine = int.Parse(
                        m.Groups[1].Value,
                        System.Globalization.CultureInfo.InvariantCulture);
                }
                continue;
            }
            if (line.StartsWith('\\'))
            {
                continue;
            }
            if (line.StartsWith('-'))
            {
                continue; // lado velho — nunca ancorável
            }
            // contexto (' ') ou adição ('+'): linha existe no arquivo novo.
            lines.NewLines.Add(newLine);
            if (line.StartsWith('+'))
            {
                lines.AddedLines.Add(newLine);
            }
            newLine++;
        }
        return lines;
    }

    /// <summary>Monta o índice a partir dos arquivos selecionados.</summary>
    public static DiffIndex Build(IEnumerable<ScmPullRequestFile> files)
    {
        var idx = new DiffIndex();
        foreach (var f in files)
        {
            if (f.Path.Length > 0)
            {
                idx._files[f.Path] = ParsePatch(f.Patch);
            }
        }
        return idx;
    }

    /// <summary>
    /// SHA-256 do conteúdo do diff (paths + status + patches ordenados) —
    /// chave de dedup de re-review (SPEC-140 RF-003).
    /// </summary>
    public static string HashOf(IEnumerable<ScmPullRequestFile> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            sb.Append(f.Path).Append('\n')
                .Append(f.PreviousPath ?? "").Append('\n')
                .Append(f.Status).Append('\n')
                .Append(f.Patch ?? "").Append("\n\n");
        }
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static readonly string[] GeneratedPathPatterns =
    [
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml",
        "poetry.lock", "cargo.lock", "go.sum", "composer.lock",
        "packages.lock.json", "gemfile.lock", ".min.js", ".min.css",
        ".generated.", ".designer.cs", ".g.cs", ".snap",
        "vendor/", "node_modules/", "dist/", ".svg",
    ];

    /// <summary>
    /// SPEC-140 RF-005: heurística "relevante" — lock files, vendor,
    /// node_modules, minified, generated e binários ficam de fora quando
    /// o diff estoura os caps.
    /// </summary>
    public static bool IsProbablyGenerated(ScmPullRequestFile file)
    {
        var p = file.Path.ToLowerInvariant();
        if (GeneratedPathPatterns.Any(
                m => p.Contains(m, StringComparison.Ordinal)))
        {
            return true;
        }
        // patch omitido pelo provider (binário/grande demais) em arquivo
        // com muitas adições — baixa prioridade na seleção.
        return file.Patch is null && file.Additions > 400;
    }

    /// <summary>
    /// Seleciona os arquivos a revisar respeitando
    /// <paramref name="maxFiles"/> e <paramref name="maxDiffBytes"/> —
    /// não-generated primeiro; retorna (selecionados, partial).
    /// </summary>
    public static (IReadOnlyList<ScmPullRequestFile> Selected, bool Partial)
        SelectForReview(IReadOnlyList<ScmPullRequestFile> files,
            int maxFiles, int maxDiffBytes)
    {
        var ordered = files
            .OrderBy(f => IsProbablyGenerated(f) ? 1 : 0)
            .ThenByDescending(f => f.Additions + f.Deletions)
            .ToList();
        var selected = new List<ScmPullRequestFile>();
        var bytes = 0;
        var partial = false;
        foreach (var f in ordered)
        {
            var size = Encoding.UTF8.GetByteCount(f.Patch ?? "") + 128;
            if (selected.Count >= maxFiles || bytes + size > maxDiffBytes)
            {
                partial = true;
                continue;
            }
            selected.Add(f);
            bytes += size;
        }
        return (selected, partial || selected.Count < files.Count);
    }

    /// <summary>
    /// Texto do diff p/ o prompt: cabeçalho <c>diff --git</c> + patch por
    /// arquivo (já limitado pela seleção).
    /// </summary>
    public static string Render(IEnumerable<ScmPullRequestFile> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files)
        {
            var old = f.PreviousPath ?? f.Path;
            sb.Append("diff --git a/").Append(old).Append(" b/")
                .Append(f.Path).Append('\n');
            if (f.PreviousPath is not null)
            {
                sb.Append("rename from ").Append(f.PreviousPath).Append('\n')
                    .Append("rename to ").Append(f.Path).Append('\n');
            }
            sb.Append("--- a/").Append(old).Append('\n')
                .Append("+++ b/").Append(f.Path).Append('\n');
            sb.Append(f.Patch ?? "(patch unavailable — binary or too large)")
                .Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Linhas âncora de um arquivo do diff.</summary>
    public sealed class FileLines
    {
        /// <summary>Todas as linhas do lado novo presentes no patch.</summary>
        public HashSet<int> NewLines { get; } = [];

        /// <summary>Subconjunto adicionado ('+').</summary>
        public HashSet<int> AddedLines { get; } = [];
    }

    [GeneratedRegex(@"@@\s*-\d+(?:,\d+)?\s*\+(\d+)(?:,\d+)?\s*@@")]
    private static partial Regex HunkHeader();
}