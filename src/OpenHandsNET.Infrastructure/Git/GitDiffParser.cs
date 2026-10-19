using System.Text.RegularExpressions;

using OpenHandsNET.Application.Git;

namespace OpenHandsNET.Infrastructure.Git;

/// <summary>Parses unified <c>git diff</c> output into a <see cref="GitDiffReport"/>.</summary>
public static partial class GitDiffParser
{
    /// <summary>Parses <c>git diff HEAD</c> text.</summary>
    public static GitDiffReport Parse(string output)
    {
        var files = new List<GitDiffFile>();
        string? path = null;
        var status = "modified";
        var hunks = new List<GitHunk>();
        var content = new System.Text.StringBuilder();
        var oldStart = 0; var oldLines = 0; var newStart = 0; var newLines = 0;
        var inHunk = false;

        void FlushHunk()
        {
            if (inHunk)
            {
                hunks.Add(new GitHunk(
                    oldStart, oldLines, newStart, newLines,
                    content.ToString().TrimEnd('\n')));
                content.Clear();
                inHunk = false;
            }
        }

        void FlushFile()
        {
            FlushHunk();
            if (path is not null)
            {
                files.Add(new GitDiffFile(path, status, hunks.ToList()));
                hunks.Clear();
            }
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FlushFile();
                path = null;
                status = "modified";
                var m = DiffGitHeader().Match(line);
                if (m.Success)
                {
                    path = m.Groups[2].Value;
                }

                continue;
            }

            if (line.StartsWith("new file mode", StringComparison.Ordinal))
            {
                status = "added";
                continue;
            }

            if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
            {
                status = "deleted";
                continue;
            }

            if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                status = "renamed";
                path = line["rename to ".Length..];
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var p = line[4..].Trim();
                if (p == "/dev/null")
                {
                    status = "deleted";
                }
                else
                {
                    path = p.StartsWith("b/", StringComparison.Ordinal) ? p[2..] : p;
                }

                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                FlushHunk();
                var m = HunkHeader().Match(line);
                if (m.Success)
                {
                    oldStart = int.Parse(m.Groups[1].Value);
                    oldLines = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
                    newStart = int.Parse(m.Groups[3].Value);
                    newLines = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1;
                    inHunk = true;
                }

                continue;
            }

            if (inHunk)
            {
                content.AppendLine(line);
            }
        }

        FlushFile();

        var adds = files.Sum(f => f.Hunks.Sum(h =>
            h.Content.Split('\n').Count(l => l.StartsWith('+') && !l.StartsWith("+++"))));
        var dels = files.Sum(f => f.Hunks.Sum(h =>
            h.Content.Split('\n').Count(l => l.StartsWith('-') && !l.StartsWith("---"))));
        return new GitDiffReport(files, adds, dels);
    }

    [GeneratedRegex(@"^diff --git a/(.+) b/(.+)$")]
    private static partial Regex DiffGitHeader();

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeader();
}