using OpenHandsNET.Application.Git;

namespace OpenHandsNET.Infrastructure.Git;

/// <summary>Parses <c>git log --format=%H%x1f%an%x1f%aI%x1f%s%x1e</c> output.</summary>
public static class GitLogParser
{
    /// <summary>Parses NUL-free field-separated log records.</summary>
    public static IReadOnlyList<GitLogEntry> Parse(string output)
    {
        var entries = new List<GitLogEntry>();
        foreach (var rec in output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = rec.Trim('\n').Split('\x1f');
            if (f.Length < 4)
            {
                continue;
            }

            entries.Add(new GitLogEntry(
                f[0], f[1],
                DateTimeOffset.TryParse(f[2], out var d) ? d : DateTimeOffset.UnixEpoch,
                f[3]));
        }

        return entries;
    }
}