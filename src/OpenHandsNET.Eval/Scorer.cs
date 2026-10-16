namespace OpenHandsNET.Eval;

/// <summary>
/// SPEC-39 RF-003: scoring + diff de baseline + gate de regressão
/// (&gt;5% drop ou pass→fail = regressão). Asserts <c>skipped</c>
/// (llm_judge sem juiz) ficam fora do score objetivo.
/// </summary>
public static class Scorer
{
    /// <summary>Score 0..1 de uma task = média ponderada dos asserts objetivos.</summary>
    public static double TaskScore(IReadOnlyList<AssertResult> asserts)
    {
        var objective = asserts.Where(a => !a.Skipped).ToList();
        if (objective.Count == 0)
        {
            return 0;
        }

        var w = objective.Sum(a => a.Weight);
        return w <= 0 ? 0 : objective
            .Sum(a => (a.Pass ? a.Weight : 0)) / w;
    }

    /// <summary>Score da suite = média ponderada das tasks.</summary>
    public static double SuiteScore(
        IReadOnlyList<EvalResult> results, IReadOnlyList<EvalTask> tasks)
    {
        var weights = tasks.ToDictionary(t => t.Name, t => t.Weight);
        var total = results.Sum(r => weights.GetValueOrDefault(r.Name, 1));
        if (total <= 0)
        {
            return 0;
        }

        return results.Sum(r =>
            r.Score * weights.GetValueOrDefault(r.Name, 1)) / total;
    }

    /// <summary>
    /// Compara resultados atuais vs baseline; devolve nomes das tasks que
    /// regrediram (drop &gt; 5% no score ou pass→fail).
    /// </summary>
    public static IReadOnlyList<Regression> Regressions(
        IReadOnlyList<EvalResult> current,
        IReadOnlyList<EvalResult> baseline)
    {
        var prev = baseline.ToDictionary(r => r.Name);
        var regressions = new List<Regression>();
        foreach (var r in current)
        {
            if (!prev.TryGetValue(r.Name, out var b))
            {
                continue;
            }

            var drop = b.Score - r.Score;
            if (drop > 0.05 || (b.Pass && !r.Pass))
            {
                regressions.Add(new Regression(r.Name, b.Score, r.Score));
            }
        }

        return regressions;
    }
}

/// <summary>Uma task que regrediu vs baseline.</summary>
public sealed record Regression(string Task, double BaselineScore, double Score);
