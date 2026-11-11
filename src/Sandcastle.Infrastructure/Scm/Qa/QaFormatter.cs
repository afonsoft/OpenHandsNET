using System.Globalization;
using System.Text;

using Sandcastle.Application.Scm;

namespace Sandcastle.Infrastructure.Scm.Qa;

/// <summary>
/// SPEC-142 RF-003 — comentário markdown do QA postado no PR: veredicto,
/// classificação, tabela de passos e links de evidência auth'd. O
/// status <c>sandcastle/qa</c> é advisory — nunca bloqueia merge por
/// si só.
/// </summary>
public static class QaFormatter
{
    private const string Success = "success";

    /// <summary>
    /// Comentário do run: <c>PASS|FAIL|PARTIAL</c> + passos + evidência.
    /// <paramref name="evidence"/> carrega pares (nome, url auth'd);
    /// até <paramref name="inlineShots"/> screenshots viram imagem
    /// inline quando o provider suporta.
    /// </summary>
    public static string Report(
        QaRunRecord run, IReadOnlyList<QaStepRecord> steps,
        IReadOnlyList<(string Name, string Url)> evidence,
        int inlineShots)
    {
        var (emoji, label) = run.Verdict switch
        {
            QaVerdicts.Pass => ("✅", "PASS"),
            QaVerdicts.Fail => ("❌", "FAIL"),
            _ => ("⚠️", "PARTIAL"),
        };
        var b = new StringBuilder();
        b.AppendLine(
            $"## Sandcastle QA — {emoji} {label} (advisory)");
        b.AppendLine();
        b.AppendLine(
            $"**Classification:** `{run.Classification ?? QaClassifications.Unknown}`"
            + $" · **Plan:** {steps.Count(s => s.Action is "exec" or "browser")}"
            + " test steps · **Head:** `"
            + run.HeadSha[..Math.Min(7, run.HeadSha.Length)] + "`");

        var duration = run.StartedAt is { } st && run.FinishedAt is { } fin
            ? fin - st : (TimeSpan?)null;
        if (duration is { } d)
        {
            b.Append(" · **Duration:** ")
                .Append(d.Minutes).Append("m ")
                .Append(d.Seconds).Append('s');
        }
        b.AppendLine();
        if (run.Verdict == QaVerdicts.Partial)
        {
            b.AppendLine("> ⚠️ PARTIAL — some steps could not be exercised"
                + " (caps, app start or browser availability). Review the"
                + " skipped steps below.");
        }
        b.AppendLine();
        AppendStepsTable(b, steps);
        AppendEvidence(b, evidence, inlineShots);
        b.AppendLine("**Full report:** `/api/v1/qa/runs/" + run.Id + "`");
        b.AppendLine();
        b.AppendLine("<sub>`" + QaDefaults.StatusContext
            + "` is advisory — it never blocks merge on its own."
            + " QA only exercised the PR head; the branch was not modified."
            + "</sub>");
        return b.ToString();
    }

    /// <summary>Tabela markdown dos passos de teste (até 12 linhas).</summary>
    private static void AppendStepsTable(
        StringBuilder b, IReadOnlyList<QaStepRecord> steps)
    {
        b.AppendLine("| # | Step | Target | Result |");
        b.AppendLine("|---|------|--------|--------|");
        var shown = 0;
        foreach (var s in steps.Where(s =>
            s.Action is "exec" or "browser" or "repro"))
        {
            var mark = s.Outcome switch
            {
                QaStepOutcomes.Ok => "✅",
                QaStepOutcomes.Fail => "❌",
                QaStepOutcomes.InfraFail => "🚫",
                _ => "⏭️",
            };
            var target = (s.Target ?? "").Replace('|', '╱');
            var detail = s.Outcome == QaStepOutcomes.Skipped
                ? "skipped" : $"{s.Outcome} ({s.DurationMs ?? 0}ms)";
            b.Append("| ").Append(s.Number).Append(" | `")
                .Append(s.Action).Append("` | ")
                .Append(target.Length > 60 ? target[..57] + "…" : target)
                .Append(" | ").Append(mark).Append(' ')
                .Append(detail).AppendLine(" |");
            if (++shown >= 12)
            {
                b.AppendLine("| … | *(remaining steps in the run report)* | | |");
                break;
            }
        }
        if (shown == 0)
        {
            b.AppendLine("| — | *(no test steps executed)* | | |");
        }
        b.AppendLine();
    }

    /// <summary>Links de evidência — até <paramref name="inlineShots"/> imagens inline.</summary>
    private static void AppendEvidence(
        StringBuilder b,
        IReadOnlyList<(string Name, string Url)> evidence,
        int inlineShots)
    {
        if (evidence.Count == 0)
        {
            return;
        }
        b.AppendLine("**Evidence** (requires Sandcastle auth)");
        var shots = 0;
        foreach (var (name, url) in evidence)
        {
            var isImage = name.EndsWith(".png",
                StringComparison.OrdinalIgnoreCase);
            if (isImage && shots < inlineShots)
            {
                b.AppendLine(
                    string.Create(CultureInfo.InvariantCulture,
                        $"![{name}]({url})"));
                shots++;
            }
            else
            {
                b.AppendLine($"- [{name}]({url})");
            }
        }
        b.AppendLine();
    }

    /// <summary>
    /// Commit status <c>sandcastle/qa</c> para o veredicto — retorna
    /// <c>(state, description)</c> p/ <c>IScmProvider.SetCommitStatusAsync</c>.
    /// <paramref name="passCondition"/>: <c>completed</c> → success
    /// quando o run terminou (veredicto não condiciona); <c>passed</c>
    /// → success somente com PASS. Run falho → <c>error</c>.
    /// </summary>
    public static (string State, string Description) CommitStatus(
        string passCondition, string? verdict, bool runFailed) =>
        runFailed switch
        {
            true => ("error", "qa pipeline failed"),
            _ => passCondition == QaDefaults.PassConditionPassed
                ? verdict switch
                {
                    QaVerdicts.Pass => (Success, "qa: pass"),
                    QaVerdicts.Fail => ("failure", "qa: fail"),
                    _ => ("failure", "qa: partial"),
                }
                : verdict switch
                {
                    QaVerdicts.Pass => (Success, "qa: pass"),
                    QaVerdicts.Fail => (Success, "qa: fail"),
                    _ => (Success, "qa: partial"),
                },
        };
}