using System.Text;
using System.Text.RegularExpressions;

namespace Sandcastle.Cli.Services;

/// <summary>
/// SPEC-150 — utilitários de diff para <c>handoff</c>/<c>pickup</c> no
/// lado do CLI: numstat, split por arquivo, detecção de binário (nunca
/// trafega blob — o path vai no metadata), blocklist dura de secrets
/// (<c>.env*</c>, <c>*.pem</c>, <c>id_rsa*</c> — sem opt-out) e
/// síntese de new-file patch para untracked.
/// </summary>
public static partial class HandoffDiffTool
{
    /// <summary>Teto do patch (KB) — Handoff:MaxDiffKb do servidor.</summary>
    public const int MaxDiffKb = 512;

    /// <summary>Máx por arquivo untracked sintetizado (bytes UTF-8).</summary>
    public const int MaxUntrackedBytes = 512 * 1024;

    /// <summary>
    /// Path bate na blocklist dura de secrets (qualquer componente do
    /// path: <c>.env*</c>, <c>*.pem</c>, <c>id_rsa*</c>).
    /// </summary>
    public static bool IsSecretPath(string path)
    {
        var name = path.Split('/', '\\')[^1];
        return name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Seção de arquivo num unified diff.</summary>
    public sealed record FileSection(string Path, string Text, bool Binary);

    /// <summary>
    /// Splita o diff nas seções <c>diff --git</c> e marca binárias
    /// (contêm <c>Binary files … differ</c> ou <c>GIT binary patch</c>).
    /// </summary>
    public static IReadOnlyList<FileSection> SplitSections(string diff)
    {
        var sections = new List<FileSection>();
        var matches = DiffGitPattern().Matches(diff);
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count
                ? matches[i + 1].Index : diff.Length;
            var text = diff[start..end];
            var path = matches[i].Groups[2].Value;
            var binary = text.Contains("Binary files ",
                StringComparison.Ordinal)
                || text.Contains("GIT binary patch",
                    StringComparison.Ordinal);
            sections.Add(new FileSection(path, text, binary));
        }

        return sections;
    }

    /// <summary>
    /// Remove as seções binárias do patch (nunca trafegam blob) e devolve
    /// os paths para o metadata (<c>binary_files</c>).
    /// </summary>
    public static (string Patch, IReadOnlyList<string> BinaryFiles)
        StripBinary(string diff)
    {
        var sections = SplitSections(diff);
        var binaries = sections.Where(s => s.Binary)
            .Select(s => s.Path).ToList();
        var patch = string.Concat(
            sections.Where(s => !s.Binary).Select(s => s.Text));
        return (patch, binaries);
    }

    /// <summary>(files, +, -) aproximado das seções não-binárias.</summary>
    public static (int Files, int Additions, int Deletions) Stat(
        string diff)
    {
        var sections = SplitSections(diff).Where(s => !s.Binary).ToList();
        var adds = 0;
        var dels = 0;
        foreach (var s in sections)
        {
            foreach (var line in s.Text.Split('\n'))
            {
                if (line.StartsWith('+')
                    && !line.StartsWith("+++", StringComparison.Ordinal))
                {
                    adds++;
                }
                else if (line.StartsWith('-')
                    && !line.StartsWith("---", StringComparison.Ordinal))
                {
                    dels++;
                }
            }
        }

        return (sections.Count, adds, dels);
    }

    /// <summary>
    /// New-file patch sintético para arquivo untracked (texto);
    /// <paramref name="content"/> com <c>\0</c> vira placeholder binário.
    /// </summary>
    public static string NewFilePatch(string path, string content)
    {
        var sb = new StringBuilder();
        sb.Append("diff --git a/").Append(path).Append(" b/").Append(path)
            .Append('\n');
        sb.Append("new file mode 100644\n");
        sb.Append("index 0000000..0000000\n");
        if (content.Contains('\0'))
        {
            sb.Append("Binary files /dev/null and b/").Append(path)
                .Append(" differ\n");
            return sb.ToString();
        }

        var lines = content.Replace("\r\n", "\n").Split('\n');
        var count = content.Length == 0 ? 0 : lines.Length;
        if (count > 0 && lines[^1].Length == 0)
        {
            count--; // newline final não é linha de conteúdo
        }

        sb.Append("--- /dev/null\n");
        sb.Append("+++ b/").Append(path).Append('\n');
        sb.Append("@@ -0,0 +1,").Append(count).Append(" @@\n");
        for (var i = 0; i < count; i++)
        {
            sb.Append('+').Append(lines[i]).Append('\n');
        }

        if (content.Length > 0 && !content.EndsWith('\n'))
        {
            sb.Append("\\ No newline at end of file\n");
        }

        return sb.ToString();
    }

    /// <summary>Unified diff mínimo reconhecível.</summary>
    public static bool LooksLikeDiff(string? text) =>
        text is { Length: > 0 } && text.Contains("diff --git ",
            StringComparison.Ordinal);

    /// <summary>Paths com hunks <c>.rej</c> listados pelo apply.</summary>
    public static IReadOnlyList<string> ParseRejects(string findOutput) =>
        findOutput.Split('\n',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Where(l => l.EndsWith(".rej", StringComparison.Ordinal))
            .Select(l => l.TrimStart('.', '/'))
            .ToList();

    [GeneratedRegex(
        @"diff --git a/(\S+) b/(\S+)", RegexOptions.Multiline)]
    private static partial Regex DiffGitPattern();
}