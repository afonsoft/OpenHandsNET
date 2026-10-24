using System.Text.RegularExpressions;

using OpenHandsNET.Application.Git;

namespace OpenHandsNET.Infrastructure.Git;

/// <summary>Parses unified <c>git diff</c> output into a <see cref="GitDiffReport"/>.</summary>
public static partial class GitDiffParser
{
    /// <summary>Parses <c>git diff HEAD</c> text.</summary>
    public static GitDiffReport Parse(string output)
    {
        var parser = new Parser();
        foreach (var raw in output.Split('\n'))
        {
            parser.Feed(raw.TrimEnd('\r'));
        }

        return parser.Finish();
    }

    private sealed class Parser
    {
        private readonly List<GitDiffFile> _files = new();
        private readonly List<GitHunk> _hunks = new();
        private readonly System.Text.StringBuilder _content = new();
        private string? _path;
        private string _status = "modified";
        private int _oldStart, _oldLines, _newStart, _newLines;
        private bool _inHunk;

        public void Feed(string line)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                StartFile(line);
                return;
            }

            if (TryMeta(line))
            {
                return;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                StartHunk(line);
                return;
            }

            if (_inHunk)
            {
                _content.AppendLine(line);
            }
        }

        public GitDiffReport Finish()
        {
            FlushFile();
            var adds = _files.Sum(f => f.Hunks.Sum(h =>
                h.Content.Split('\n').Count(l => l.StartsWith('+') && !l.StartsWith("+++"))));
            var dels = _files.Sum(f => f.Hunks.Sum(h =>
                h.Content.Split('\n').Count(l => l.StartsWith('-') && !l.StartsWith("---"))));
            return new GitDiffReport(_files, adds, dels);
        }

        private void StartFile(string line)
        {
            FlushFile();
            _path = null;
            _status = "modified";
            var m = DiffGitHeader().Match(line);
            if (m.Success)
            {
                _path = m.Groups[2].Value;
            }
        }

        private bool TryMeta(string line)
        {
            if (line.StartsWith("new file mode", StringComparison.Ordinal))
            {
                _status = "added";
                return true;
            }

            if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
            {
                _status = "deleted";
                return true;
            }

            if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                _status = "renamed";
                _path = line["rename to ".Length..];
                return true;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                ApplyNewPath(line);
                return true;
            }

            return line.StartsWith("--- ", StringComparison.Ordinal);
        }

        private void ApplyNewPath(string line)
        {
            var p = line[4..].Trim();
            if (p == "/dev/null")
            {
                _status = "deleted";
            }
            else
            {
                _path = p.StartsWith("b/", StringComparison.Ordinal) ? p[2..] : p;
            }
        }

        private void StartHunk(string line)
        {
            FlushHunk();
            var m = HunkHeader().Match(line);
            if (m.Success)
            {
                _oldStart = int.Parse(m.Groups[1].Value);
                _oldLines = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
                _newStart = int.Parse(m.Groups[3].Value);
                _newLines = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1;
                _inHunk = true;
            }
        }

        private void FlushHunk()
        {
            if (_inHunk)
            {
                _hunks.Add(new GitHunk(
                    _oldStart, _oldLines, _newStart, _newLines,
                    _content.ToString().TrimEnd('\n')));
                _content.Clear();
                _inHunk = false;
            }
        }

        private void FlushFile()
        {
            FlushHunk();
            if (_path is not null)
            {
                _files.Add(new GitDiffFile(_path, _status, _hunks.ToList()));
                _hunks.Clear();
            }
        }
    }

    [GeneratedRegex(@"^diff --git a/(.+) b/(.+)$")]
    private static partial Regex DiffGitHeader();

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeader();
}