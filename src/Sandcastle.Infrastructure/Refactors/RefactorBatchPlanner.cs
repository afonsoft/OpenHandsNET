using Sandcastle.Application.Refactors;

namespace Sandcastle.Infrastructure.Refactors;

/// <summary>
/// SPEC-144 RF-001: deterministic batch planner (pure — no IO). Given the
/// repo file list and a dependency edge map (<c>file → files it depends
/// on</c>), produces topologically-ordered batches:
/// <list type="number">
/// <item>Kahn levels: level(f) = 0 when f has no deps; else 1 + max dep
///   level. Cycles are broken by force-assigning the least-blocked
///   remaining file (its intra-cycle deps degrade to batch-local).</item>
/// <item>Levels are packed into batches of ≈ <c>batchSize</c> files —
///   never exceeding <c>maxBatchFiles</c> nor <c>maxBatches</c>
///   (overflow merges into the last batch).</item>
/// <item><c>depends_on[]</c> = batches holding this batch's file deps
///   (always strictly earlier — levels are non-decreasing).</item>
/// <item><c>independent</c> mode requires zero file overlap between
///   batches; overlap &gt; 0 forces <c>stacked</c> (§9 guardrail).</item>
/// <item><c>stacked</c> mode chains every batch on its predecessor so the
///   executor serializes and each PR bases on the previous head.</item>
/// </list>
/// </summary>
public static class RefactorBatchPlanner
{
    /// <summary>
    /// Orders <paramref name="files"/> (repo-relative, '/'-separated)
    /// into dependency-respecting batches. <paramref name="edges"/> maps a
    /// file to the files it depends on; edges to files outside
    /// <paramref name="files"/> are ignored, and dependencies inside the
    /// same batch are satisfied implicitly (they ship in the same PR).
    /// </summary>
    public static RefactorPlanOutput Plan(
        string repository,
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> edges,
        string mode,
        int batchSize,
        int maxBatchFiles,
        int maxBatches,
        string? verifyCommand,
        int startN = 1)
    {
        var warnings = new List<string>();
        var distinct = files
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0)
        {
            return new RefactorPlanOutput([], mode, warnings);
        }

        // 1) Kahn levels (cycle-safe).
        var levels = AssignLevels(distinct, edges);

        // 2) pack levels → batches.
        var size = Math.Max(1, batchSize);
        var hardSize = Math.Max(size, maxBatchFiles);
        var groups = PackLevels(distinct, levels, size, hardSize,
            Math.Max(1, maxBatches));

        // 3) depends_on from the file→batch map.
        var batchOf = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < groups.Count; i++)
        {
            foreach (var f in groups[i])
            {
                batchOf[f] = i;
            }
        }

        var deps = new List<int>[groups.Count];
        for (var i = 0; i < groups.Count; i++)
        {
            var set = new SortedSet<int>();
            foreach (var f in groups[i])
            {
                if (!edges.TryGetValue(f, out var ds))
                {
                    continue;
                }

                foreach (var d in ds)
                {
                    if (batchOf.TryGetValue(d, out var j) && j != i)
                    {
                        set.Add(j);
                    }
                }
            }
            deps[i] = set.ToList();
        }

        // 4) overlap guard — a file must appear in exactly one batch.
        var effective = mode;
        var overlap = FileOverlap(groups);
        if (overlap.Count > 0 && effective == RefactorModes.Independent)
        {
            effective = RefactorModes.Stacked;
            warnings.Add(
                $"file overlap across batches ({overlap.Count} file(s)) "
                + "forced mode=stacked — never two parallel PRs touching "
                + "the same file");
        }

        // 5) stacked → linear chain on the predecessor batch.
        if (effective == RefactorModes.Stacked)
        {
            for (var i = 1; i < deps.Length; i++)
            {
                if (!deps[i].Contains(i - 1))
                {
                    deps[i] = deps[i].Append(i - 1)
                        .Distinct().OrderBy(x => x).ToList();
                }
            }
        }

        // depends_on carries batch n values (1-based, offset by startN
        // so multi-repo plans keep global pointers) — never local
        // indexes.
        var batches = groups.Select((g, i) => new RefactorBatchInput(
            repository,
            g,
            deps[i].Select(d => d + startN).ToList(),
            DescribeBatch(g),
            RiskOf(g.Count, size),
            verifyCommand)).ToList();
        return new RefactorPlanOutput(batches, effective, warnings);
    }

    /// <summary>
    /// Overlap guard (§9): files appearing in more than one batch.
    /// Exposed for validation of hand-edited <c>plan_json</c> payloads.
    /// </summary>
    public static IReadOnlySet<string> FileOverlap(
        IReadOnlyList<IReadOnlyList<string>> batches)
    {
        var seen = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        var dup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in batches)
        {
            foreach (var f in batch)
            {
                if (!seen.TryAdd(f, 1))
                {
                    dup.Add(f);
                }
            }
        }
        return dup;
    }

    /// <summary>
    /// Overlap guard over persisted <see cref="RefactorBatchInput"/>
    /// batches (same rule, plan-shaped input).
    /// </summary>
    public static IReadOnlySet<string> FileOverlap(
        IReadOnlyList<RefactorBatchInput> batches) =>
        FileOverlap(batches.Select(b => b.Files).ToList());

    /// <summary>
    /// State transition table — single source of truth used by the
    /// executor + service so every transition stays spec-valid.
    /// </summary>
    public static bool CanTransition(string from, string to) =>
        (from, to) switch
        {
            (RefactorBatchStates.Pending, RefactorBatchStates.Ready) => true,
            (RefactorBatchStates.Pending, RefactorBatchStates.Blocked) => true,
            (RefactorBatchStates.Ready, RefactorBatchStates.Running) => true,
            (RefactorBatchStates.Running, RefactorBatchStates.Verifying) => true,
            (RefactorBatchStates.Running, RefactorBatchStates.Failed) => true,
            (RefactorBatchStates.Verifying, RefactorBatchStates.PrOpen) => true,
            (RefactorBatchStates.Verifying, RefactorBatchStates.Failed) => true,
            (RefactorBatchStates.PrOpen, RefactorBatchStates.Merged) => true,
            (RefactorBatchStates.PrOpen, RefactorBatchStates.Failed) => true,
            (RefactorBatchStates.Blocked, RefactorBatchStates.Pending) => true,
            (RefactorBatchStates.Failed, RefactorBatchStates.Ready) => true,
            _ => false,
        };

    /// <summary>Kahn levels with deterministic cycle-breaking.</summary>
    private static Dictionary<string, int> AssignLevels(
        List<string> files,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> edges)
    {
        var known = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var depsOf = files.ToDictionary(
            f => f,
            f => edges.TryGetValue(f, out var ds)
                ? ds.Where(known.Contains).ToHashSet(
                    StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var level = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        var remaining = depsOf.Keys.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var depth = 0;
        while (remaining.Count > 0)
        {
            var frontier = remaining
                .Where(f => depsOf[f].All(d => level.ContainsKey(d)))
                .ToList();
            if (frontier.Count == 0)
            {
                // Cycle — pick the remaining file with the fewest unmet
                // deps (deterministic tiebreak by path).
                var f = remaining
                    .OrderBy(r => depsOf[r].Count(d => !level.ContainsKey(d)))
                    .ThenBy(r => r, StringComparer.OrdinalIgnoreCase)
                    .First();
                frontier = [f];
            }

            foreach (var f in frontier)
            {
                level[f] = depth;
                remaining.Remove(f);
            }
            depth++;
        }
        return level;
    }

    /// <summary>Packs levels into ≤maxBatches groups of ≈size files.</summary>
    private static List<List<string>> PackLevels(
        List<string> files, Dictionary<string, int> levels,
        int size, int hardSize, int maxBatches)
    {
        // Effective pack size — the hint is a soft target, never above
        // the hard per-batch file ceiling.
        var packSize = Math.Max(1, Math.Min(size, hardSize));
        var groups = new List<List<string>>();
        var current = new List<string>();
        var currentLevel = -1;
        foreach (var f in files.OrderBy(f => levels[f])
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var lv = levels[f];
            // New batch on level change (dep layer boundary) or when
            // full — same-level files are mutually independent, so
            // splitting a level keeps batches parallelizable and can
            // never create a forward dep.
            if (current.Count > 0
                && (current.Count >= packSize || lv != currentLevel))
            {
                groups.Add(current);
                current = [];
            }
            current.Add(f);
            currentLevel = lv;
        }
        if (current.Count > 0)
        {
            groups.Add(current);
        }

        // Cap batch count — overflow folds into the tail (still ordered;
        // deps can only point backwards, so merging tailward is safe).
        while (groups.Count > maxBatches)
        {
            var tail = groups[^1];
            groups.RemoveAt(groups.Count - 1);
            groups[^1].AddRange(tail);
        }
        return groups;
    }

    /// <summary>Risk heuristic — bigger batches review worse.</summary>
    internal static string RiskOf(int fileCount, int size) =>
        fileCount >= Math.Max(size * 2, 40)
            ? RefactorRisks.High
            : fileCount >= size ? RefactorRisks.Medium : RefactorRisks.Low;

    /// <summary>Human description — top dirs covered by the batch.</summary>
    private static string DescribeBatch(IReadOnlyList<string> files)
    {
        var dirs = files
            .Select(TopDir)
            .GroupBy(d => d)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(3)
            .Select(g => g.Key)
            .ToList();
        var label = string.Join(", ", dirs);
        return $"{files.Count} file(s) under {label}";
    }

    /// <summary>First path segment (or "." for root files).</summary>
    internal static string TopDir(string path)
    {
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? "." : path[..slash];
    }
}