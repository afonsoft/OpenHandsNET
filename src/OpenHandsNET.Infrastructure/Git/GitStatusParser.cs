using OpenHandsNET.Application.Git;

namespace OpenHandsNET.Infrastructure.Git;

/// <summary>
/// Parses <c>git status --porcelain=v2 --branch -uall</c> output into
/// <see cref="GitChanges"/> buckets (SPEC-12 RF-002).
/// </summary>
public static class GitStatusParser
{
    /// <summary>Parses porcelain v2 output.</summary>
    public static GitChanges Parse(string output)
    {
        var branch = "";
        var staged = new List<GitFileStatus>();
        var modified = new List<GitFileStatus>();
        var untracked = new List<GitFileStatus>();
        var conflicts = new List<GitFileStatus>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                branch = line["# branch.head ".Length..].Trim();
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("? ", StringComparison.Ordinal))
            {
                untracked.Add(new GitFileStatus(line[2..], "untracked"));
                continue;
            }

            if (line.StartsWith("! ", StringComparison.Ordinal))
            {
                continue; // ignored
            }

            var fields = line.Split(' ', 2);
            var head = fields[0];
            var rest = fields.Length > 1 ? fields[1] : "";

            if (head is "u")
            {
                // u <xy> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>
                var parts = rest.Split(' ', 10);
                conflicts.Add(new GitFileStatus(
                    parts.Length == 10 ? parts[9] : rest, "conflict"));
                continue;
            }

            if (head is "1" or "2")
            {
                // 1 <xy> <sub> <mH> <mI> <mW> <hH> <hI> <path>  (7 meta fields)
                // 2 <xy> <sub> <mH> <mI> <mW> <hH> <hI> <X><score> <path><sep><orig>
                var limit = head == "2" ? 9 : 8;
                var parts = rest.Split(' ', limit);
                if (parts.Length < limit)
                {
                    continue;
                }

                var xy = parts[0];
                var path = parts[limit - 1];
                string? orig = null;
                // type 2 (rename): "path\torigPath" or "path origPath" after score field
                if (head == "2" && path.Contains('\t'))
                {
                    var pp = path.Split('\t');
                    path = pp[0];
                    orig = pp.Length > 1 ? pp[1] : null;
                }
                else if (head == "2")
                {
                    // porcelain v2 rename uses NUL; textual fallback: score field + two paths
                    var splitIdx = path.LastIndexOf(' ');
                    if (splitIdx > 0)
                    {
                        orig = path[(splitIdx + 1)..];
                        path = path[..splitIdx];
                    }
                }

                var x = xy.Length > 0 ? xy[0] : '.';
                var y = xy.Length > 1 ? xy[1] : '.';
                if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D'))
                {
                    conflicts.Add(new GitFileStatus(path, "conflict", orig));
                    continue;
                }

                if (x is 'M' or 'A' or 'D' or 'R' or 'C')
                {
                    staged.Add(new GitFileStatus(path, MapCode(x), orig));
                }

                if (y is 'M' or 'D')
                {
                    modified.Add(new GitFileStatus(path, MapCode(y), orig));
                }
            }
        }

        return new GitChanges(
            branch.Length == 0 ? "HEAD" : branch,
            staged, modified, untracked, conflicts);
    }

    private static string MapCode(char c) => c switch
    {
        'A' => "added",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        _ => "modified",
    };
}