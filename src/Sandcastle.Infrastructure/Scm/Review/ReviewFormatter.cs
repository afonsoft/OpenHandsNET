using System.Text;

using Sandcastle.Application.Scm;

namespace Sandcastle.Infrastructure.Scm.Review;

/// <summary>
/// SPEC-140 RF-003 — renderização do review: body do summary (findings
/// agrupados por severidade, 🔴🟠🟡🟢), body dos comentários inline e o
/// mapeamento pass-condition → commit status (sempre advisory — nunca
/// required por si só; branch rules do repo decidem).
/// </summary>
public static class ReviewFormatter
{
    /// <summary>Emoji por severidade (SPEC-140 RF-003).</summary>
    public static string Emoji(string severity) => severity switch
    {
        ReviewSeverities.Critical => "🔴",
        ReviewSeverities.Important => "🟠",
        ReviewSeverities.Nit => "🟢",
        _ => "🟡",
    };

    /// <summary>Título curto da severidade.</summary>
    public static string Label(string severity) => severity switch
    {
        ReviewSeverities.Critical => "Critical",
        ReviewSeverities.Important => "Important",
        ReviewSeverities.Nit => "Nit",
        _ => "Suggestion",
    };

    /// <summary>Ordem de severidade p/ agrupamento (menor = pior).</summary>
    public static int Rank(string severity) => severity switch
    {
        ReviewSeverities.Critical => 0,
        ReviewSeverities.Important => 1,
        ReviewSeverities.Suggestion => 2,
        _ => 3,
    };

    /// <summary>Body de um comentário inline.</summary>
    public static string InlineBody(ParsedFinding f)
    {
        var sb = new StringBuilder();
        sb.Append(Emoji(f.Severity)).Append(" **")
            .Append(Label(f.Severity)).Append("** · ")
            .Append('`').Append(f.Kind).Append('`');
        if (!string.IsNullOrWhiteSpace(f.Title))
        {
            sb.Append(" — **").Append(f.Title.Trim()).Append("**");
        }
        sb.Append("\n\n").Append(f.Body.Trim());
        if (!string.IsNullOrWhiteSpace(f.SuggestedPatch))
        {
            sb.Append("\n\n```suggestion\n")
                .Append(f.SuggestedPatch.TrimEnd()).Append("\n```");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Summary da review: header + contagens + findings agrupados.
    /// <paramref name="overflow"/> = findings acima do cap inline (viram
    /// linha no summary, nunca comentário — SPEC-140 NFR).
    /// </summary>
    public static string Summary(
        IReadOnlyList<ParsedFinding> findings,
        IReadOnlyList<ParsedFinding> overflow,
        int filesReviewed, int filesTotal, bool partial,
        bool inlineAnchored)
    {
        var sb = new StringBuilder();
        sb.Append("## Sandcastle Review\n\n");
        if (findings.Count == 0)
        {
            sb.Append("✅ No issues found");
        }
        else
        {
            var counts = findings
                .GroupBy(f => f.Severity)
                .ToDictionary(g => g.Key, g => g.Count());
            sb.Append("**").Append(findings.Count).Append(" finding")
                .Append(findings.Count == 1 ? "" : "s").Append("** — ");
            sb.Append(string.Join(" · ",
                new[] { ReviewSeverities.Critical, ReviewSeverities.Important,
                    ReviewSeverities.Suggestion, ReviewSeverities.Nit }
                .Where(counts.ContainsKey)
                .Select(s => $"{Emoji(s)} {counts[s]} {Label(s).ToLowerInvariant()}")));
        }
        sb.Append(" in ").Append(filesReviewed).Append(" file")
            .Append(filesReviewed == 1 ? "" : "s").Append(".\n");
        if (partial)
        {
            sb.Append("\n> ⚠️ **Partial review** — diff exceeded size caps; ")
                .Append(filesTotal - filesReviewed)
                .Append(" file(s) skipped (generated/large files "
                    + "prioritized out).\n");
        }
        if (!inlineAnchored && findings.Count > 0)
        {
            sb.Append("\n> ℹ️ Inline comments are not supported by this "
                + "provider — findings listed below.\n");
        }

        foreach (var group in findings
            .GroupBy(f => f.Severity)
            .OrderBy(g => Rank(g.Key)))
        {
            sb.Append("\n### ").Append(Emoji(group.Key)).Append(' ')
                .Append(Label(group.Key)).Append('\n');
            foreach (var f in group)
            {
                sb.Append("\n- `").Append(f.Path);
                if (f.Line is { } l)
                {
                    sb.Append(':').Append(l);
                }
                sb.Append('`');
                if (!string.IsNullOrWhiteSpace(f.Title))
                {
                    sb.Append(" — ").Append(f.Title.Trim());
                }
                sb.Append(" — ").Append(FirstLine(f.Body));
            }
            sb.Append('\n');
        }

        if (overflow.Count > 0)
        {
            sb.Append("\n<details><summary>Findings beyond the inline "
                + "comment cap</summary>\n");
            foreach (var f in overflow)
            {
                sb.Append("\n- `").Append(f.Path);
                if (f.Line is { } l)
                {
                    sb.Append(':').Append(l);
                }
                sb.Append("` — ").Append(FirstLine(f.Body));
            }
            sb.Append("\n</details>\n");
        }

        sb.Append("\n---\n<sub>Advisory — <code>sandcastle/review</code> "
            + "never blocks merges by itself.</sub>");
        return sb.ToString();
    }

    /// <summary>
    /// Body do comentário único de fallback (providers sem review API):
    /// summary + findings completos.
    /// </summary>
    public static string FallbackBody(
        IReadOnlyList<ParsedFinding> findings, int filesReviewed,
        int filesTotal, bool partial)
    {
        var sb = new StringBuilder(
            Summary(findings, [], filesReviewed, filesTotal, partial,
                inlineAnchored: false));
        if (findings.Count > 0)
        {
            sb.Append("\n\n<details><summary>Full findings</summary>\n");
            foreach (var f in findings)
            {
                sb.Append("\n#### ").Append(Emoji(f.Severity)).Append(' ')
                    .Append('`').Append(f.Path);
                if (f.Line is { } l)
                {
                    sb.Append(':').Append(l);
                }
                sb.Append("`\n\n").Append(f.Body.Trim()).Append('\n');
                if (!string.IsNullOrWhiteSpace(f.SuggestedPatch))
                {
                    sb.Append("\n```suggestion\n")
                        .Append(f.SuggestedPatch.TrimEnd()).Append("\n```\n");
                }
            }
            sb.Append("\n</details>");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Pass-condition → commit status state (SPEC-140 RF-003): default
    /// <c>completed</c> = sempre <c>success</c> (advisory);
    /// <c>no_critical</c>/<c>no_findings</c> são opt-in e podem emitir
    /// <c>failure</c> — nunca <c>required</c> por si só.
    /// </summary>
    public static (string State, string Description) CommitStatus(
        string passCondition, IReadOnlyList<ParsedFinding> findings,
        bool partial)
    {
        var critical = findings.Count(
            f => f.Severity == ReviewSeverities.Critical);
        var desc = findings.Count == 0
            ? "No issues found (advisory)"
            : $"{findings.Count} finding(s)"
                + (critical > 0 ? $", {critical} critical" : "")
                + (partial ? " — partial diff" : "") + " (advisory)";
        var state = passCondition switch
        {
            ReviewDefaults.PassConditionNoCritical when critical > 0
                => "failure",
            ReviewDefaults.PassConditionNoFindings
                when findings.Count > 0 => "failure",
            _ => "success",
        };
        return (state, desc.Length > 140 ? desc[..140] : desc);
    }

    private static string FirstLine(string body)
    {
        var line = body.Split('\n', 2)[0].Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}