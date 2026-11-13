using Microsoft.AspNetCore.Mvc;

using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Eval;
using Sandcastle.Application.Identity;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// SPEC-152 — benchmark/eval harness (<c>/api/v1/evals</c>).
/// Todas as rotas exigem <c>perm:evals.run</c> (admin+ por default —
/// eval é caro). <c>POST /evals</c> é idempotente por chave
/// (suite+model+route+tasks_path+n) enquanto a run estiver ativa;
/// runs acima de <c>Eval:EstimateGateTasks</c> exigem
/// <c>estimate_acknowledged=true</c> (400 <c>estimate_required</c>
/// com a estimativa embutida).
/// </summary>
public static class EvalEndpoints
{
    private const string PermEvalsRun = "perm:evals.run";
    private const string RunNotFound = "eval run not found";

    /// <summary>Mapeia os endpoints de eval.</summary>
    public static IEndpointRouteBuilder MapEvalEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/evals")
            .WithTags("Evals");

        // POST /evals {suite, model?, route_override?, n?,
        //   tasks_path?, estimate_acknowledged?} → 202.
        group.MapPost("", CreateEvalAsync)
            .RequireAuthorization(PermEvalsRun);

        // POST /evals/estimate {suite, n?, model?, tasks_path?} → 200.
        group.MapPost("/estimate", EstimateEvalAsync)
            .RequireAuthorization(PermEvalsRun);

        // GET /evals/scorecard?suite=X&last=N → 200.
        group.MapGet("/scorecard", ScorecardAsync)
            .RequireAuthorization(PermEvalsRun);

        group.MapGet("", ListEvalsAsync)
            .RequireAuthorization(PermEvalsRun);

        group.MapGet("/{id:guid}", GetEvalAsync)
            .RequireAuthorization(PermEvalsRun);

        group.MapGet("/{id:guid}/results", GetResultsAsync)
            .RequireAuthorization(PermEvalsRun);

        group.MapPost("/{id:guid}/cancel", CancelEvalAsync)
            .RequireAuthorization(PermEvalsRun);

        return app;
    }

    private static async Task<IResult> CreateEvalAsync(
        [FromBody] CreateEvalRunRequest body, HttpContext http,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        IEvalService evals, CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var org = await orgs.ResolveAsync(
            u, http.Request.Headers["X-Org-Id"].ToString(), ct);
        var (created, error, gate) = await evals.CreateAsync(
            body, u.UserId, org?.OrgId, ct);
        if (created is not null)
        {
            return Results.Accepted($"/api/v1/evals/{created.RunId}",
                new
                {
                    id = created.RunId,
                    status = created.Status,
                    n_tasks = created.NTasks,
                    estimated_cost_usd = created.EstimatedCostUsd,
                });
        }

        return error switch
        {
            "estimate_required" => Results.Json(new
            {
                error = "estimate_required",
                detail = "run exceeds Eval:EstimateGateTasks — "
                    + "review the estimate and resend with "
                    + "estimate_acknowledged=true",
                estimate = ToEstimateDto(gate!),
            }, statusCode: 400),
            { Length: > 0 } e when e.StartsWith(
                "daily_cost_exceeded", StringComparison.Ordinal)
                => SandcastleProblemFactory.Create(http, 429,
                    "daily eval cost cap reached"
                    + $" (Eval:MaxDailyCostUsd={e.Split(':')[1]})"),
            { Length: > 0 } e when e.StartsWith(
                "max_tasks_exceeded", StringComparison.Ordinal)
                => SandcastleProblemFactory.Create(http, 400,
                    $"n exceeds Eval:MaxTasks ({e.Split(':')[1]})"),
            { Length: > 0 } e when e.StartsWith(
                "suite_load", StringComparison.Ordinal)
                => SandcastleProblemFactory.Create(http, 400, e),
            _ => SandcastleProblemFactory.Create(http, 400,
                error ?? "invalid eval request"),
        };
    }

    private static async Task<IResult> EstimateEvalAsync(
        [FromBody] EvalEstimateRequest body, HttpContext http,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        IEvalService evals, CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var org = await orgs.ResolveAsync(
            u, http.Request.Headers["X-Org-Id"].ToString(), ct);
        var est = await evals.EstimateAsync(body, org?.OrgId, ct);
        return est is null
            ? SandcastleProblemFactory.Create(http, 400,
                "invalid suite or tasks unavailable")
            : Results.Ok(ToEstimateDto(est));
    }

    private static async Task<IResult> ScorecardAsync(
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IEvalService evals,
        [FromQuery] string? suite, [FromQuery] int? last,
        CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var org = await orgs.ResolveAsync(
            u, http.Request.Headers["X-Org-Id"].ToString(), ct);
        var rows = await evals.ScorecardAsync(
            org?.OrgId, suite, last ?? 10, ct);
        return Results.Ok(new
        {
            suite,
            items = rows.Select(r => new
            {
                model = r.Model,
                route = r.Route,
                runs = r.Runs,
                tasks = r.Tasks,
                resolved = r.Resolved,
                resolved_pct = r.ResolvedPct,
                total_tokens = r.TotalTokens,
                total_cost_usd = r.TotalCostUsd,
                last_run_at = r.LastRunAt,
            }).ToList(),
        });
    }

    private static async Task<IResult> ListEvalsAsync(
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IEvalService evals,
        [FromQuery] string? suite, [FromQuery] string? status,
        [FromQuery] string? cursor, [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var orgId = (await orgs.ResolveAsync(
            u, http.Request.Headers["X-Org-Id"].ToString(), ct))?.OrgId;
        if (orgId is null)
        {
            return SandcastleProblemFactory.Create(http, 404, "no org");
        }

        var page = await evals.ListAsync(
            orgId.Value, suite, status, cursor, limit ?? 50, ct);
        return Results.Ok(new
        {
            items = page.Items.Select(ToRunDto).ToList(),
            next_page_id = page.NextPageId,
        });
    }

    private static async Task<IResult> GetEvalAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IEvalService evals,
        CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var orgId = (await orgs.ResolveAsync(
            u, http.Request.Headers["X-Org-Id"].ToString(), ct))?.OrgId;
        if (orgId is null)
        {
            return SandcastleProblemFactory.Create(http, 404, "no org");
        }

        var run = await evals.GetAsync(orgId.Value, id, ct);
        return run is null
            ? SandcastleProblemFactory.Create(http, 404, RunNotFound)
            : Results.Ok(ToRunDto(run));
    }

    private static async Task<IResult> GetResultsAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IEvalService evals,
        CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var orgId = (await orgs.ResolveAsync(
            u, http.Request.Headers["X-Org-Id"].ToString(), ct))?.OrgId;
        if (orgId is null)
        {
            return SandcastleProblemFactory.Create(http, 404, "no org");
        }

        var rows = await evals.ListResultsAsync(orgId.Value, id, ct);
        return rows is null
            ? SandcastleProblemFactory.Create(http, 404, RunNotFound)
            : Results.Ok(new
            {
                items = rows.Select(r => new
                {
                    id = r.Id,
                    task_id = r.TaskId,
                    status = r.Status,
                    verdict = r.Verdict,
                    session_id = r.SessionId,
                    duration_s = r.DurationS,
                    tokens = r.Tokens,
                    cost_usd = r.CostUsd,
                    log_digest = r.LogDigest,
                    error = r.Error,
                    finished_at = r.FinishedAt,
                }).ToList(),
            });
    }

    private static async Task<IResult> CancelEvalAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IEvalService evals,
        CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var orgId = (await orgs.ResolveAsync(
            u, http.Request.Headers["X-Org-Id"].ToString(), ct))?.OrgId;
        if (orgId is null)
        {
            return SandcastleProblemFactory.Create(http, 404, "no org");
        }

        return await evals.CancelAsync(orgId.Value, id, ct) switch
        {
            null => Results.Accepted(
                null, new { status = EvalRunStatuses.Cancelled }),
            "not_found" => SandcastleProblemFactory.Create(
                http, 404, RunNotFound),
            _ => SandcastleProblemFactory.Create(http, 409,
                "run already in a terminal state"),
        };
    }

    private static object ToRunDto(EvalRunSummary r) => new
    {
        id = r.Id,
        org_id = r.OrgId,
        suite = r.Suite,
        n_tasks = r.NTasks,
        model = r.Model,
        route_override = r.RouteOverride,
        status = r.Status,
        resolved_n = r.ResolvedN,
        failed_n = r.FailedN,
        error_n = r.ErrorN,
        timeout_n = r.TimeoutN,
        score_pct = r.ScorePct,
        estimated_cost_usd = r.EstimatedCostUsd,
        actual_cost_usd = r.ActualCostUsd,
        error = r.Error,
        created_at = r.CreatedAt,
        finished_at = r.FinishedAt,
    };

    private static object ToEstimateDto(EvalEstimate e) => new
    {
        suite = e.Suite,
        n = e.N,
        model = e.Model,
        avg_prompt_tokens = e.AvgPromptTokens,
        avg_completion_tokens = e.AvgCompletionTokens,
        estimated_cost_usd = e.EstimatedCostUsd,
        pricing_known = e.PricingKnown,
        requires_acknowledgement = e.RequiresAcknowledgement,
        warning = e.Warning,
    };
}