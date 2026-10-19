namespace OpenHandsNET.Client.Services;

/// <summary>
/// SPEC-42 RF-007: fuzzy matcher do quick-open (port do
/// <c>IdeQuickOpenFuzzy.cs</c> do fork MIT) — score por match de
/// subsequência com bônus por fronteira de segmento e penalidade de gap.
/// </summary>
public static class IdeQuickOpenFuzzy
{
    /// <summary>Score do match; 0 quando não há subsequência completa.</summary>
    public static int Score(string query, string candidate)
    {
        if (query.Length == 0)
        {
            return 1;
        }

        var q = query.AsSpan();
        var c = candidate.AsSpan();
        var score = 0;
        var qi = 0;
        var lastHit = -2;
        for (var i = 0; i < c.Length && qi < q.Length; i++)
        {
            if (char.ToLowerInvariant(c[i]) != char.ToLowerInvariant(q[qi]))
            {
                continue;
            }

            score += 10;
            // Fronteira de segmento (início, '/', '_', '-', '.', camelCase).
            if (i == 0 || c[i - 1] is '/' or '_' or '-' or '.'
                || char.IsUpper(c[i]) && !char.IsUpper(c[i - 1]))
            {
                score += 5;
            }

            // Consecutivo vale mais que esparso.
            if (i == lastHit + 1)
            {
                score += 8;
            }

            lastHit = i;
            qi++;
        }

        return qi == q.Length
            // Paths curtos e com match tardio perdem pontos.
            ? score - (c.Length - q.Length) / 4 : 0;
    }

    /// <summary>Top-N candidatos ordenados por score decrescente.</summary>
    public static IReadOnlyList<string> Top(
        string query, IEnumerable<string> candidates, int take = 20) =>
        candidates
            .Select(c => (path: c, score: Score(query, c)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.path.Length)
            .Take(take)
            .Select(x => x.path)
            .ToList();
}