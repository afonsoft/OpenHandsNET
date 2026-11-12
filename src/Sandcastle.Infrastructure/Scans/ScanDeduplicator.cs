using Sandcastle.Application.Scans;

namespace Sandcastle.Infrastructure.Scans;

/// <summary>
/// SPEC-143 RF-002: two-pass finding dedup.
/// Pass 1 (exact): key <c>(repo, path, start_line, kind)</c> — duplicates
/// merge keeping the max severity and highest-confidence description.
/// Pass 2 (near): same <c>(repo,path,kind)</c> with overlapping/adjacent
/// line ranges (±<see cref="NearLineWindow"/>) merge when normalized
/// titles collide or descriptions share ≥
/// <see cref="NearSimilarity"/> word-Jaccard similarity.
/// </summary>
public static class ScanDeduplicator
{
    /// <summary>Line-distance window for near-duplicate matching.</summary>
    public const int NearLineWindow = 10;

    /// <summary>Min word-Jaccard similarity for near merges.</summary>
    public const double NearSimilarity = 0.6;

    /// <summary>
    /// Deduplicates <paramref name="findings"/> (exact then near),
    /// returning the merged list plus the merge count for stats.
    /// </summary>
    public static (IReadOnlyList<ScanFindingInput> Kept, int Merged)
        Deduplicate(IReadOnlyList<ScanFindingInput> findings)
    {
        // --- Pass 1: exact (repo,path,start_line,kind) ---
        var exact = new Dictionary<ExactKey, ScanFindingInput>();
        var merged = 0;
        foreach (var f in findings)
        {
            var key = new ExactKey(
                f.Repository, NormPath(f.Path), f.StartLine ?? -1, f.Kind);
            if (exact.TryGetValue(key, out var kept))
            {
                exact[key] = Merge(kept, f);
                merged++;
            }
            else
            {
                exact[key] = f;
            }
        }

        // --- Pass 2: near-duplicate within (repo,path,kind) ---
        var groups = exact.Values
            .GroupBy(f => (f.Repository, Path: NormPath(f.Path), f.Kind))
            .OrderBy(g => g.Key.Repository, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Path, StringComparer.Ordinal);
        var result = new List<ScanFindingInput>();
        foreach (var g in groups)
        {
            var ordered = g.OrderBy(f => f.StartLine ?? int.MaxValue)
                .ToList();
            var cluster = new List<ScanFindingInput>();
            foreach (var f in ordered)
            {
                ScanFindingInput? hit = null;
                foreach (var c in cluster)
                {
                    if (LinesClose(c, f) && TextSimilar(c, f))
                    {
                        hit = c;
                        break;
                    }
                }

                if (hit is null)
                {
                    cluster.Add(f);
                }
                else
                {
                    var i = cluster.IndexOf(hit);
                    cluster[i] = Merge(hit, f);
                    merged++;
                }
            }

            result.AddRange(cluster);
        }

        return (result, merged);
    }

    /// <summary>
    /// Keep the max-severity copy, prefer the longer description and the
    /// highest confidence between the two.
    /// </summary>
    private static ScanFindingInput Merge(
        ScanFindingInput a, ScanFindingInput b)
    {
        var winner = ScanSeverities.Rank(a.Severity)
            <= ScanSeverities.Rank(b.Severity) ? a : b;
        var loser = ReferenceEquals(winner, a) ? b : a;
        var bestDesc = winner.Description.Length
            >= loser.Description.Length
            ? winner.Description : loser.Description;
        return winner with
        {
            Description = bestDesc,
            Confidence = MaxNull(winner.Confidence, loser.Confidence),
            Title = winner.Title ?? loser.Title,
            EndLine = winner.EndLine ?? loser.EndLine,
            StartLine = MinLine(winner.StartLine, loser.StartLine),
            RemediationHint = winner.RemediationHint
                ?? loser.RemediationHint,
            Payoff = winner.Payoff ?? loser.Payoff,
        };
    }

    private static int? MinLine(int? a, int? b) =>
        a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    private static double? MaxNull(double? a, double? b) =>
        a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static bool LinesClose(ScanFindingInput a, ScanFindingInput b)
    {
        if (a.StartLine is null || b.StartLine is null)
        {
            // Lineless findings of same file+kind are near-suspect.
            return true;
        }

        var aEnd = a.EndLine ?? a.StartLine.Value;
        var bEnd = b.EndLine ?? b.StartLine.Value;
        // Overlap or adjacency within the window.
        return a.StartLine.Value <= bEnd + NearLineWindow
            && b.StartLine.Value <= aEnd + NearLineWindow;
    }

    private static bool TextSimilar(ScanFindingInput a, ScanFindingInput b)
    {
        if (a.Title is not null && b.Title is not null
            && string.Equals(NormText(a.Title), NormText(b.Title),
                StringComparison.Ordinal))
        {
            return true;
        }

        var wa = WordSet(a.Description);
        var wb = WordSet(b.Description);
        if (wa.Count == 0 || wb.Count == 0)
        {
            return false;
        }

        var inter = wa.Intersect(wb).Count();
        var union = wa.Union(wb).Count();
        return union > 0 && (double)inter / union >= NearSimilarity;
    }

    private static HashSet<string> WordSet(string? text) =>
        text is null
            ? []
            : new HashSet<string>(
                text.Split(
                    [' ', '\n', '\t', '.', ',', ';', ':', '(', ')', '[',
                     ']', '{', '}', '/', '\\', '\'', '"', '`'],
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
                .Select(w => w.ToLowerInvariant())
                .Where(w => w.Length > 2),
                StringComparer.Ordinal);

    private static string NormPath(string p) =>
        p.Replace('\\', '/').Trim().ToLowerInvariant();

    private static string NormText(string t) =>
        string.Concat(t.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)));

    private readonly record struct ExactKey(
        string Repository, string Path, int StartLine, string Kind);
}