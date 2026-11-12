using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Identity;

namespace Sandcastle.Infrastructure.Agent.Routing;

/// <summary>
/// SPEC-149 RF-001: single LLM-routing choke point. With org
/// <c>routing_rules</c> it resolves the role's candidate list through
/// <see cref="RouteEvaluator"/> (policy → circuit → pin → budget →
/// weight), audits <c>llm.route_decision</c>, and walks the plan on
/// <see cref="NextFallbackAsync"/> (auditing <c>llm.route_fallback</c>
/// and opening the <see cref="LlmCircuitBreaker"/>). Without rules it is
/// byte-identical to the SPEC-23 pass-through (RoleModels → preferred →
/// default; <c>AgentLlm:FallbackModels</c> chain) — zero regression.
/// Scoped deps (rules/budget/audit) resolve per call via
/// <paramref name="scopeFactory"/> — null keeps the router pure
/// pass-through (unit tests, no DB).
/// </summary>
public sealed class ModelRouter(
    IModelCatalog catalog,
    IOptions<AgentLlmOptions> options,
    IServiceScopeFactory? scopeFactory = null,
    LlmCircuitBreaker? breaker = null,
    IOptions<RouterOptions>? routerOptions = null,
    ILogger<ModelRouter>? logger = null) : IModelRouter
{
    private readonly LlmCircuitBreaker _breaker =
        breaker ?? new LlmCircuitBreaker();
    private readonly ILogger<ModelRouter> _logger =
        logger ?? NullLogger<ModelRouter>.Instance;
    private int _weightCursor;

    /// <inheritdoc />
    public async Task<ModelSelection> SelectAsync(
        Guid conversationId, ModelRole role, string? preferredModel,
        CancellationToken ct)
    {
        var llmRole = LlmRoles.FromModelRole(role);
        var ctx = new LlmRouteContext(
            ConversationId: conversationId,
            PreferredModel: preferredModel);

        if (scopeFactory is not null && conversationId != Guid.Empty)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var rules = scope.ServiceProvider
                .GetService<IRoutingRulesService>();
            if (rules is not null)
            {
                var conv = await rules.ContextForConversationAsync(
                    conversationId, ct);
                if (conv is not null)
                {
                    ctx = conv with
                    {
                        PreferredModel =
                            preferredModel ?? conv.PreferredModel,
                    };
                }
            }
        }

        var sel = await ResolveAsync(llmRole, ctx, ct);
        return sel ?? throw new LlmNoRouteException(
            LlmRoles.ToWire(llmRole), "no eligible candidate");
    }

    /// <inheritdoc />
    public async Task<ModelSelection?> ResolveAsync(
        LlmRole role, LlmRouteContext ctx, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var cfg = options.Value;
        var router = routerOptions?.Value ?? new RouterOptions();
        var roleWire = LlmRoles.ToWire(role);

        AsyncServiceScope? scope = scopeFactory?.CreateAsyncScope();
        try
        {
            IRoutingRulesService? rulesSvc = null;
            IUsageBudgetService? budget = null;
            IAuditService? audit = null;
            if (scope is { } s)
            {
                rulesSvc = s.ServiceProvider
                    .GetService<IRoutingRulesService>();
                budget = s.ServiceProvider
                    .GetService<IUsageBudgetService>();
                audit = s.ServiceProvider.GetService<IAuditService>();
            }

            // Ctx mínimo (só ConversationId) ⇒ completa org/user/pin
            // pela conversa — callers sem acesso ao record ficam cobertos.
            if (rulesSvc is not null && ctx.OrgId is null
                && ctx.ConversationId is { } cid && cid != Guid.Empty)
            {
                var conv = await rulesSvc.ContextForConversationAsync(
                    cid, ct);
                if (conv is not null)
                {
                    ctx = ctx with
                    {
                        OrgId = conv.OrgId,
                        UserId = ctx.UserId ?? conv.UserId,
                        PreferredModel =
                            ctx.PreferredModel ?? conv.PreferredModel,
                    };
                }
            }

            var doc = rulesSvc is null
                ? null
                : await rulesSvc.GetAsync(ctx.OrgId, ct);
            var candidates = doc?.CandidatesFor(roleWire);

            // Pass-through — regras ausentes/vazias ou role não
            // governada ⇒ comportamento idêntico ao SPEC-23.
            if (doc is null || candidates is null or { Count: 0 })
            {
                var sel = await PassThroughAsync(
                    role, ctx.PreferredModel, cfg, ct);
                await RecordDecisionAsync(audit, roleWire, ctx, sel.Model,
                    sel.Provider, "pass_through",
                    evaluated: null, sw, ct);
                return sel;
            }

            // Orçamento — só consulta quando algum candidato marca
            // budget_tier (evita query extra no caminho comum).
            double? remainingPct = null;
            var cheapBelow = doc.CheapWhenBelowPct
                ?? router.CheapWhenBelowPct;
            if (budget is not null && ctx.OrgId is { } oid
                && candidates.Any(c => c.BudgetTier is not null))
            {
                var uid = Guid.TryParse(ctx.UserId, out var parsed)
                    ? parsed : Guid.Empty;
                var snap = await budget.GetBudgetAsync(
                    uid, oid, orgScope: true, ct);
                if (snap is { MonthlyLimit: > 0, Remaining: { } rem })
                {
                    remainingPct = 100.0 * (double)rem
                        / snap.MonthlyLimit.Value;
                }
            }

            var eval = RouteEvaluator.Evaluate(
                candidates, doc,
                LlmRoles.IsPinnable(role), ctx.PreferredModel,
                remainingPct, cheapBelow, _breaker,
                Interlocked.Increment(ref _weightCursor));

            if (eval.Ordered.Count == 0)
            {
                await RecordDecisionAsync(audit, roleWire, ctx, null, null,
                    "no_route", eval.Evaluated, sw, ct);
                return null;
            }

            var first = eval.Ordered[0];
            var info = await catalog.GetAsync(first.Model, ct);
            var provider = first.Provider ?? info?.Provider ?? cfg.Provider;
            var cooldown = doc.CircuitOpenSec ?? router.CircuitOpenSec;

            await RecordDecisionAsync(audit, roleWire, ctx, first.Model,
                provider, eval.Reason, eval.Evaluated, sw, ct);

            return new ModelSelection(
                first.Model, provider, info?.ContextWindow,
                Attempt: 0, FallbackFrom: null,
                ApiKeyRef: first.ApiKeyRef ?? "default",
                Reason: eval.Reason,
                Route: new RoutePlan(
                    roleWire, ctx.OrgId, ctx.UserId, ctx.PreferredModel,
                    eval.Ordered, RulesActive: true, cooldown));
        }
        finally
        {
            if (scope is { } s2)
            {
                await s2.DisposeAsync();
            }
        }
    }

    /// <inheritdoc />
    public async Task<ModelSelection?> NextFallbackAsync(
        ModelSelection current, LlmErrorKind kind, CancellationToken ct)
    {
        // Auth nunca tem fallback; overflow é tratado pelo caminho SPEC-22.
        if (kind is LlmErrorKind.Auth or LlmErrorKind.ContextOverflow)
        {
            return null;
        }

        if (current.Route is { Candidates.Count: > 0 } plan)
        {
            _breaker.ReportFailure(current.Provider, current.Model,
                TimeSpan.FromSeconds(plan.CircuitOpenSec));

            var next = plan.Candidates.Count > current.Attempt + 1
                ? plan.Candidates[current.Attempt + 1]
                : null;
            if (next is null)
            {
                return null; // cadeia esgotada ⇒ caller vira llm_no_route
            }

            var info = await catalog.GetAsync(next.Model, ct);
            var provider = next.Provider ?? info?.Provider
                ?? options.Value.Provider;
            await RecordFallbackAsync(plan, current, next.Model, kind, ct);
            return new ModelSelection(
                next.Model, provider, info?.ContextWindow,
                current.Attempt + 1, current.Model,
                ApiKeyRef: next.ApiKeyRef ?? "default",
                Reason: $"fallback:{kind.ToString().ToLowerInvariant()}",
                Route: plan);
        }

        return await LegacyFallbackAsync(current, kind, ct);
    }

    /// <summary>SPEC-23 pass-through fallback — <c>AgentLlm:FallbackModels</c> walk.</summary>
    private async Task<ModelSelection?> LegacyFallbackAsync(
        ModelSelection current, LlmErrorKind kind, CancellationToken ct)
    {
        var chain = options.Value.FallbackModels;
        if (chain.Count == 0)
        {
            return null;
        }

        var idx = chain.FindIndex(m =>
            string.Equals(m, current.Model,
                StringComparison.OrdinalIgnoreCase));
        string? next;
        if (idx < 0)
        {
            next = chain[0];
        }
        else if (idx + 1 < chain.Count)
        {
            next = chain[idx + 1];
        }
        else
        {
            next = null;
        }

        if (next is null
            || string.Equals(next, current.Model,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var info = await catalog.GetAsync(next, ct);
        return new ModelSelection(
            next, info?.Provider, info?.ContextWindow,
            current.Attempt + 1, current.Model);
    }

    /// <summary>SPEC-23 selection — RoleModels → preferred → default.</summary>
    private async Task<ModelSelection> PassThroughAsync(
        LlmRole role, string? preferred, AgentLlmOptions cfg,
        CancellationToken ct)
    {
        var modelId = ResolveRoleModel(role, cfg) ?? preferred ?? cfg.Model;
        var info = await catalog.GetAsync(modelId, ct);
        return new ModelSelection(
            modelId, info?.Provider, info?.ContextWindow,
            Attempt: 0, FallbackFrom: null,
            ApiKeyRef: "default", Reason: "pass_through");
    }

    private async Task RecordDecisionAsync(
        IAuditService? audit, string role, LlmRouteContext ctx,
        string? model, string? provider, string reason,
        IReadOnlyList<RouteEvaluator.CandidateStatus>? evaluated,
        Stopwatch sw, CancellationToken ct)
    {
        if (audit is null)
        {
            return;
        }

        try
        {
            await audit.RecordAsync(
                "llm.route_decision", ctx.UserId, ctx.OrgId,
                targetType: "llm_route", targetId: role,
                metadata: new
                {
                    role,
                    model,
                    provider,
                    reason,
                    conversation_id = ctx.ConversationId,
                    evaluated = evaluated?.Select(s => new
                    {
                        model = s.Candidate.Model,
                        provider = s.Candidate.Provider,
                        status = s.Status,
                    }).ToArray(),
                    latency_ms = sw.ElapsedMilliseconds,
                },
                cancellationToken: ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Auditoria nunca derruba a resolução de rota.
            _logger.LogWarning(e, "llm.route_decision audit failed");
        }
    }

    private async Task RecordFallbackAsync(
        RoutePlan plan, ModelSelection current, string next,
        LlmErrorKind kind, CancellationToken ct)
    {
        if (scopeFactory is null)
        {
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var audit = scope.ServiceProvider.GetService<IAuditService>();
            if (audit is null)
            {
                return;
            }

            await audit.RecordAsync(
                "llm.route_fallback", plan.UserId, plan.OrgId,
                targetType: "llm_route", targetId: plan.Role,
                metadata: new
                {
                    role = plan.Role,
                    from = current.Model,
                    to = next,
                    reason = kind.ToString().ToLowerInvariant(),
                    attempt = current.Attempt + 1,
                },
                cancellationToken: ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "llm.route_fallback audit failed");
        }
    }

    private static string? ResolveRoleModel(
        LlmRole role, AgentLlmOptions cfg)
    {
        var key = role switch
        {
            LlmRole.Code or LlmRole.Chat => "worker",
            LlmRole.Plan => "planner",
            LlmRole.Review => "reviewer",
            LlmRole.Analysis => "analysis",
            LlmRole.Summarizer => "summarizer",
            LlmRole.Subagent => "subagent",
            _ => null,
        };
        return key is not null
            && cfg.RoleModels.TryGetValue(key, out var id)
            && id.Length > 0
            ? id
            : null;
    }
}