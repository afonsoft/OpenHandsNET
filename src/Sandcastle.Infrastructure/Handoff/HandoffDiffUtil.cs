using System.Text;
using System.Text.RegularExpressions;

namespace Sandcastle.Infrastructure.Handoff;

/// <summary>
/// SPEC-150 — helpers de unified diff compartilhados pelo apply (worker)
/// e pelo package (endpoint): numstat, placeholders binários, síntese de
/// new-file patch para arquivos untracked e blocklist de secrets (dura —
/// sem opt-out, SPEC-150 §9).
/// </summary>
public static partial class HandoffDiffUtil
{
    /// <summary>Marcador de patch sintético binário (paridade `git diff`).</summary>
    public const string BinaryPlaceholder = "binary files differ";

    /// <summary>Cap do corpo de um hunk rejeitado no evento (chars).</summary>
    public const int MaxRejectChars = 4096;

    /// <summary>Cap de leitura de arquivo untracked (256 KB).</summary>
    public const int MaxUntrackedBytes = 256 * 1024;

    /// <summary>
    /// Blocklist dura de secrets — nunca entram num patch de handoff em
    /// nenhuma direção (SPEC-150 §9, sem flag de opt-out):
    /// <c>.env*</c>, <c>*.pem</c>, <c>id_rsa*</c> por basename.
    /// </summary>
    public static bool IsSecretPath(string path)
    {
        var name = Path.GetFileName(path.Trim());
        return name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Patch tem cara de unified diff (diff --git | ---/+++ par).</summary>
    public static bool LooksLikeUnifiedDiff(string? patch)
    {
        if (string.IsNullOrWhiteSpace(patch))
        {
            return false;
        }

        return patch.Contains("diff --git ", StringComparison.Ordinal)
            || (patch.Contains("--- ", StringComparison.Ordinal)
                && patch.Contains("+++ ", StringComparison.Ordinal))
            || patch.Contains("Binary files ", StringComparison.Ordinal);
    }

    /// <summary>(files, additions, deletions) de um unified diff.</summary>
    public static (int Files, int Additions, int Deletions) Numstat(string patch)
    {
        var files = 0;
        long adds = 0, dels = 0;
        var inHunk = false;
        foreach (var raw in patch.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                files++;
                inHunk = false;
                continue;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                inHunk = true;
                continue;
            }

            if (!inHunk)
            {
                continue;
            }

            if (line.StartsWith('+') && !line.StartsWith("+++"))
            {
                adds++;
            }
            else if (line.StartsWith('-') && !line.StartsWith("---"))
            {
                dels++;
            }
        }

        return (files, (int)Math.Min(adds, int.MaxValue),
            (int)Math.Min(dels, int.MaxValue));
    }

    /// <summary>
    /// Paths binários do diff — linhas <c>Binary files a/x and b/y differ</c>
    /// (o blob nunca trafega; só o path entra no metadata).
    /// </summary>
    public static IReadOnlyList<string> BinaryFilesOf(string patch)
    {
        var files = new List<string>();
        foreach (var raw in patch.Split('\n'))
        {
            var m = BinaryLine().Match(raw.TrimEnd('\r'));
            if (m.Success)
            {
                files.Add(m.Groups[1].Value);
            }
        }

        return files;
    }

    /// <summary>
    /// Sintetiza um unified diff <c>new file</c> para um arquivo untracked
    /// (não há <c>git diff</c> para untracked). Conteúdo binário →
    /// placeholder <c>Binary files … differ</c>.
    /// </summary>
    public static string NewFilePatch(string path, string content)
    {
        var safe = path.Replace('\\', '/').TrimStart('/');
        var sb = new StringBuilder();
        if (content.Contains('\0', StringComparison.Ordinal))
        {
            sb.Append("diff --git a/").Append(safe).Append(" b/")
                .Append(safe).Append('\n');
            sb.Append("Binary files /dev/null and b/").Append(safe)
                .Append(" differ\n");
            return sb.ToString();
        }

        var lines = content.Split('\n');
        // ignora newline final (split produz elemento vazio no fim).
        var count = lines.Length;
        if (count > 0 && lines[^1].Length == 0)
        {
            count--;
        }

        sb.Append("diff --git a/").Append(safe).Append(" b/")
            .Append(safe).Append('\n');
        sb.Append("new file mode 100644\n");
        sb.Append("index 0000000..0000000\n");
        sb.Append("--- /dev/null\n");
        sb.Append("+++ b/").Append(safe).Append('\n');
        sb.Append("@@ -0,0 +1,").Append(Math.Max(count, 1)).Append(" @@\n");
        for (var i = 0; i < count; i++)
        {
            sb.Append('+').Append(lines[i].TrimEnd('\r')).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Corta texto em <paramref name="maxChars"/> com marcador.</summary>
    public static string Cap(string text, int maxChars) =>
        text.Length <= maxChars
            ? text
            : string.Concat(text.AsSpan(0, Math.Max(0, maxChars)),
                "[…truncated]");

    [GeneratedRegex(@"^Binary files .* and b/(.+) differ\s*$")]
    private static partial Regex BinaryLine();
}