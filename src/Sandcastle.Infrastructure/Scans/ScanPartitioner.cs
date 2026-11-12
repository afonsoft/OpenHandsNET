using Sandcastle.Infrastructure.Indexing;

namespace Sandcastle.Infrastructure.Scans;

/// <summary>A scan partition — a set of repo-relative directories (or the
/// whole repo when the tree is small) assigned to one scan child.</summary>
public sealed record ScanPartition(
    string Repository,
    IReadOnlyList<string> Directories)
{
    /// <summary>Human-readable label for prompts/stats.</summary>
    public string Label => Directories.Count == 0
        ? "/" : string.Join(", ", Directories);
}

/// <summary>
/// SPEC-143 RF-002: partition a repo checkout into directory buckets for
/// the fan-out. Top-level directories with indexable files are counted
/// and bin-packed by file count into ≤ <c>targetPartitions</c>
/// buckets (greedy largest-first); repos with little structure collapse
/// to a single root partition (<see cref="ScanPartition.Directories"/>
/// empty = scan the whole repo).
/// </summary>
public static class ScanPartitioner
{
    /// <summary>Directories that never get their own partition.</summary>
    private static readonly HashSet<string> IgnoredTopDirs = new(
        StringComparer.OrdinalIgnoreCase)
    { "bin", "obj", "node_modules", ".git", ".vs", ".idea", "dist", "build" };

    /// <summary>
    /// Partitions <paramref name="repoRoot"/> (a checkout workdir) into
    /// ≤ <paramref name="targetPartitions"/> directory buckets.
    /// </summary>
    public static IReadOnlyList<ScanPartition> Partition(
        string repoRoot, string repository, int targetPartitions,
        int maxDirsPerPartition = 6)
    {
        var target = Math.Max(1, targetPartitions);
        var fileCounts = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        var rootFiles = 0;

        foreach (var file in RepoIndexer.EnumerateRepoFiles(repoRoot))
        {
            var rel = Path.GetRelativePath(repoRoot, file)
                .Replace('\\', '/');
            if (!RepoIndexer.AllowedExtensions.Contains(
                    Path.GetExtension(rel)))
            {
                continue;
            }

            var slash = rel.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0)
            {
                rootFiles++;
                continue;
            }

            var top = rel[..slash];
            if (IgnoredTopDirs.Contains(top))
            {
                continue;
            }

            fileCounts[top] = fileCounts.GetValueOrDefault(top) + 1;
        }

        // Small/flat trees → single root partition.
        if (fileCounts.Count <= 1 && rootFiles > 0
            || fileCounts.Count == 0)
        {
            return
            [
                new ScanPartition(repository, [])
            ];
        }

        // Greedy bin-pack: largest dir into the smallest bucket.
        var buckets = new List<(List<string> Dirs, int Total)>();
        foreach (var (dir, count) in fileCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var placed = false;
            if (buckets.Count < target)
            {
                buckets.Add(([dir], count));
                placed = true;
            }
            else
            {
                var min = 0;
                for (var i = 1; i < buckets.Count; i++)
                {
                    if (buckets[i].Total < buckets[min].Total)
                    {
                        min = i;
                    }
                }

                if (buckets[min].Dirs.Count < Math.Max(1, maxDirsPerPartition))
                {
                    buckets[min].Dirs.Add(dir);
                    buckets[min] = (buckets[min].Dirs,
                        buckets[min].Total + count);
                    placed = true;
                }
            }

            if (!placed)
            {
                // Every bucket is full — merge into the smallest anyway.
                var min = 0;
                for (var i = 1; i < buckets.Count; i++)
                {
                    if (buckets[i].Total < buckets[min].Total)
                    {
                        min = i;
                    }
                }
                buckets[min].Dirs.Add(dir);
                buckets[min] = (buckets[min].Dirs,
                    buckets[min].Total + count);
            }
        }

        // Root-level files join the smallest bucket as "." when the tree
        // has a meaningful root share.
        if (rootFiles > 0 && buckets.Count > 0)
        {
            var min = 0;
            for (var i = 1; i < buckets.Count; i++)
            {
                if (buckets[i].Total < buckets[min].Total)
                {
                    min = i;
                }
            }
            buckets[min].Dirs.Add(".");
            buckets[min] = (buckets[min].Dirs,
                buckets[min].Total + rootFiles);
        }

        return buckets
            .Where(b => b.Dirs.Count > 0)
            .Select(b => new ScanPartition(
                repository,
                b.Dirs.OrderBy(d => d, StringComparer.Ordinal).ToList()))
            .OrderBy(p => p.Label, StringComparer.Ordinal)
            .ToList();
    }
}