using Sandcastle.Application.Agent;

namespace Sandcastle.Infrastructure.Agent.Routing;

/// <summary>
/// SPEC-149 RF-001: pure candidate-ordering core of the model router.
/// Given the role's configured list + org policy + circuit state +
/// budget snapshot it produces the eligible pick order and a per-
/// candidate status trail (audited as <c>llm.route_decision</c>).
/// Order of criteria: admin policy (deny → allowlist → residency) →
/// circuit closed → user pin (pinnable roles) → budget tier → weight.
/// </summary>
internal static class RouteEvaluator
{
    /// <summary>Per-candidate outcome recorded in the audit trail.</summary>
    public sealed record CandidateStatus(
        RouteCandidate Candidate, string Status);

    /// <summary>
    /// Evaluation result: <see cref="Ordered"/> is the fallback chain
    /// (index 0 = pick); empty ⇒ <c>llm_no_route</c>.
    /// </summary>
    public sealed record Result(
        IReadOnlyList<CandidateStatus> Evaluated,
        IReadOnlyList<RouteCandidate> Ordered,
        string Reason);

    private const string Eligible = "eligible";

    /// <summary>Evaluates a role's candidate list (never throws).</summary>
    public static Result Evaluate(
        IReadOnlyList<RouteCandidate> candidates,
        RoutingRulesDocument doc,
        bool pinnable,
        string? pin,
        double? remainingBudgetPct,
        int cheapWhenBelowPct,
        LlmCircuitBreaker circuit,
        int weightCursor)
    {
        var statuses = new List<CandidateStatus>(candidates.Count);
        var eligible = new List<RouteCandidate>(candidates.Count);
        var reasons = new List<string>(3);

        var denied = doc.DenyModels.Count > 0
            ? new HashSet<string>(doc.DenyModels,
                StringComparer.OrdinalIgnoreCase)
            : null;
        var allowed = doc.AllowedModels.Count > 0
            ? new HashSet<string>(doc.AllowedModels,
                StringComparer.OrdinalIgnoreCase)
            : null;
        var internalOnly = string.Equals(doc.DataResidency,
            "internal-only", StringComparison.OrdinalIgnoreCase);

        // 1) filtros — política admin vence tudo (hard boundary).
        foreach (var c in candidates)
        {
            var status =
                denied?.Contains(c.Model) == true ? "denied" :
                allowed?.Contains(c.Model) == false ? "not_allowed" :
                internalOnly && !c.Internal ? "residency" :
                circuit.IsOpen(c.Provider, c.Model) ? "circuit_open" :
                Eligible;
            statuses.Add(new CandidateStatus(c, status));
            if (status is Eligible)
            {
                eligible.Add(c);
            }
        }

        if (eligible.Count == 0)
        {
            return new Result(statuses, [], "no_route");
        }

        // 2) pin do usuário — só em roles pinnable e só dentro da lista
        //    configurada (o pin nunca cria rota fora das regras).
        if (pinnable && !string.IsNullOrWhiteSpace(pin))
        {
            var idx = eligible.FindIndex(c =>
                string.Equals(c.Model, pin,
                    StringComparison.OrdinalIgnoreCase));
            if (idx > 0)
            {
                var c = eligible[idx];
                eligible.RemoveAt(idx);
                eligible.Insert(0, c);
                reasons.Add("pin");
            }
            else if (idx < 0)
            {
                reasons.Add(statuses.Any(s =>
                        string.Equals(s.Candidate.Model, pin,
                            StringComparison.OrdinalIgnoreCase))
                    ? "pin_filtered"
                    : "pin_unlisted");
            }
        }

        // 3) budget tier — cheap primeiro quando o saldo aperta
        //    (partição estável preserva a ordem configurada).
        if (remainingBudgetPct is { } pct && pct < cheapWhenBelowPct
            && eligible.Any(IsCheap) && eligible.Any(c => !IsCheap(c)))
        {
            eligible = [.. eligible.Where(IsCheap),
                        .. eligible.Where(c => !IsCheap(c))];
            reasons.Add("budget_tier");
        }

        // 4) weight — loteria determinística por cursor; weight 0 sai do
        //    sorteio mas continua como último recurso da cadeia.
        var weighted = eligible.Where(c => c.Weight is > 0).ToList();
        if (weighted.Count > 0)
        {
            var total = weighted.Sum(c => (long)c.Weight!.Value);
            var cursor = (int)(((weightCursor % total) + total) % total);
            RouteCandidate? picked = null;
            var acc = 0L;
            foreach (var c in weighted)
            {
                acc += c.Weight!.Value;
                if (cursor < acc)
                {
                    picked = c;
                    break;
                }
            }

            if (picked is not null
                && !ReferenceEquals(picked, eligible[0]))
            {
                eligible.Remove(picked);
                eligible.Insert(0, picked);
                reasons.Add("weight");
            }
            else if (picked is not null && eligible.Count > 1
                     && eligible.Any(c => c.Weight > 1))
            {
                reasons.Add("weight");
            }
        }

        statuses[statuses.FindIndex(
            s => ReferenceEquals(s.Candidate, eligible[0]))] =
            new CandidateStatus(eligible[0], "selected");

        return new Result(statuses, eligible,
            reasons.Count > 0 ? string.Join('+', reasons) : "order");
    }

    private static bool IsCheap(RouteCandidate c) =>
        string.Equals(c.BudgetTier, "cheap",
            StringComparison.OrdinalIgnoreCase);
}