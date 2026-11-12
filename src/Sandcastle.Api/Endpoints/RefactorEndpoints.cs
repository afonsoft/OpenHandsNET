using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Refactors;
using Sandcastle.Domain.Identity;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// SPEC-144 — batch refactors (<c>/api/v1/refactors</c>). Permissões:
/// <c>perm:refactors.run</c> (create/list/get/control); o gate de
/// aprovação do plano exige <c>perm:refactors.approve</c> (admin+) quando
/// <c>Refactor:RequirePlanApproval=true</c> — checado dentro do start
/// handler porque o requisito depende de config runtime.
/// </summary>
public static class RefactorEndpoints
{
    private const string PermRefactorsRun = "perm:refactors.run";
    private const string PlanNotFound = "refactor plan not found";

    /// <summary>Mapeia os endpoints de batch refactors.</summary>
    public static IEndpointRouteBuilder MapRefactorEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/refactors")
            .WithTags("Refactors");

        // POST /refactors {repositories[], goal, strategy?,
        //   max_concurrency?, mode?, batch_size_hint?} → 202.
        group.MapPost("", CreateRefactorAsync)
            .RequireAuthorization(PermRefactorsRun);

        group.MapGet("", ListRefactorsAsync)
            .RequireAuthorization(PermRefactorsRun);

        group.MapGet("/{id:guid}", GetRefactorAsync)
            .RequireAuthorization(PermRefactorsRun);

        group.MapGet("/{id:guid}/batches", GetBatchesAsync)
            .RequireAuthorization(PermRefactorsRun);

        // Approval gate + start (approve check inside — runtime config).
        group.MapPost("/{id:guid}/start", StartRefactorAsync)
            .RequireAuthorization(PermRefactorsRun);

        group.MapPost("/{id:guid}/pause", PauseRefactorAsync)
            .RequireAuthorization(PermRefactorsRun);

        group.MapPost("/{id:guid}/resume", ResumeRefactorAsync)
            .RequireAuthorization(PermRefactorsRun);

        group.MapPost("/{id:guid}/cancel", CancelRefactorAsync)
            .RequireAuthorization(PermRefactorsRun);

        group.MapPost("/{id:guid}/batches/{n:int}/retry",
            RetryBatchAsync).RequireAuthorization(PermRefactorsRun);

        return app;
    }

    private static async Task<IResult> CreateRefactorAsync(
        [FromBody] CreateRefactorRequest body, HttpContext http,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        IRefactorService refactors, CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var org = await orgs.ResolveAsync(
            u, http.Request.Headers["X-Org-Id"].ToString(), ct);
        try
        {
            var (created, error) = await refactors.CreateAsync(
                body, u.UserId, org?.OrgId, ct);
            return created is null
                ? SandcastleProblemFactory.Create(
                    http, 400, error ?? "invalid refactor request")
                : Results.Accepted(
                    $"/api/v1/refactors/{created.PlanId}", new
                    {
                        plan_id = created.PlanId,
                        conversation_id = created.PlannerConversationId,
                        planner_conversation_id =
                            created.PlannerConversationId,
                        status = created.Status,
                    });
        }
        catch (Application.Identity.PlanLimitExceededException ex)
        {
            return AppConversationEndpoints.PlanLimitProblem(ex.Error);
        }
    }

    private static async Task<IResult> ListRefactorsAsync(
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IRefactorService refactors,
        [FromQuery] string? status, [FromQuery] string? cursor,
        [FromQuery] int? limit, CancellationToken ct)
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

        var page = await refactors.ListAsync(
            orgId.Value, status, cursor, limit ?? 50, ct);
        return Results.Ok(new
        {
            items = page.Items.Select(ToPlanDto).ToList(),
            next_page_id = page.NextPageId,
        });
    }

    private static async Task<IResult> GetRefactorAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IRefactorService refactors,
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

        var plan = await refactors.GetAsync(orgId.Value, id, ct);
        return plan is null
            ? SandcastleProblemFactory.Create(http, 404, PlanNotFound)
            : Results.Ok(ToPlanDto(plan));
    }

    private static async Task<IResult> GetBatchesAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IRefactorService refactors,
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

        var batches = await refactors.ListBatchesAsync(
            orgId.Value, id, ct);
        return batches is null
            ? SandcastleProblemFactory.Create(http, 404, PlanNotFound)
            : Results.Ok(new
            {
                items = batches.Select(ToBatchDto).ToList(),
            });
    }

    private static async Task<IResult> StartRefactorAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IRefactorService refactors,
        AppDbContext db, CancellationToken ct)
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

        // Approval gate: caller holds refactors.approve? (role da org —
        // mesma avaliação do PermissionHandler; service users usam a
        // role do próprio registro).
        var role = u.ActorKind == ActorKind.ServiceUser
            ? u.Role
            : await db.OrgMembers.AsNoTracking()
                .Where(m => m.OrgId == orgId && m.UserId == u.UserId)
                .Select(m => m.Role)
                .FirstOrDefaultAsync(ct);
        var canApprove = RolePermissions.ForOrgRole(role)
            .Contains(Permission.RefactorsApprove);

        var error = await refactors.StartAsync(
            orgId.Value, id, u.UserId, canApprove, ct);
        return ControlResult(http, error, "start");
    }

    private static async Task<IResult> PauseRefactorAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IRefactorService refactors,
        CancellationToken ct) =>
        await ControlCoreAsync(id, http, current, orgs,
            (o, p, c) => refactors.PauseAsync(o, p, c),
            "pause", ct);

    private static async Task<IResult> ResumeRefactorAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IRefactorService refactors,
        CancellationToken ct) =>
        await ControlCoreAsync(id, http, current, orgs,
            (o, p, c) => refactors.ResumeAsync(o, p, c),
            "resume", ct);

    private static async Task<IResult> CancelRefactorAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IRefactorService refactors,
        CancellationToken ct) =>
        await ControlCoreAsync(id, http, current, orgs,
            (o, p, c) => refactors.CancelAsync(o, p, c),
            "cancel", ct);

    private static async Task<IResult> RetryBatchAsync(
        Guid id, int n, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IRefactorService refactors,
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

        var error = await refactors.RetryBatchAsync(
            orgId.Value, id, n, ct);
        return error switch
        {
            null => Results.Accepted(
                $"/api/v1/refactors/{id}", new { status = "queued" }),
            "not_found" => SandcastleProblemFactory.Create(
                http, 404, PlanNotFound),
            "batch_not_found" => SandcastleProblemFactory.Create(
                http, 404, "refactor batch not found"),
            _ => SandcastleProblemFactory.Create(http, 409,
                $"batch cannot be retried in its current state"),
        };
    }

    /// <summary>Shared org-resolution + control call for pause/resume/cancel.</summary>
    private static async Task<IResult> ControlCoreAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs,
        Func<Guid, Guid, CancellationToken, Task<string?>> action,
        string verb, CancellationToken ct)
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

        return ControlResult(http,
            await action(orgId.Value, id, ct), verb);
    }

    private static IResult ControlResult(
        HttpContext http, string? error, string verb) =>
        error switch
        {
            null => Results.Accepted(
                null, new { status = "accepted" }),
            "not_found" => SandcastleProblemFactory.Create(
                http, 404, PlanNotFound),
            "approval_required" => SandcastleProblemFactory.Create(
                http, 403,
                "plan approval requires the refactors.approve "
                + "permission"),
            "no_plan" => SandcastleProblemFactory.Create(http, 409,
                "plan has no batches yet — wait for planning"),
            _ => SandcastleProblemFactory.Create(http, 409,
                $"plan cannot {verb} in its current state"),
        };

    private static object ToPlanDto(RefactorPlanSummary p) => new
    {
        id = p.Id,
        org_id = p.OrgId,
        repositories = p.Repositories,
        goal = p.Goal,
        strategy = p.Strategy,
        mode = p.Mode,
        requested_mode = p.RequestedMode,
        max_concurrency = p.MaxConcurrency,
        batch_size_hint = p.BatchSizeHint,
        status = p.Status,
        batches_total = p.BatchesTotal,
        state_counts = p.StateCounts,
        progress_percent = p.ProgressPercent,
        planner_conversation_id = p.PlannerConversationId,
        coordinator_conversation_id = p.CoordinatorConversationId,
        warnings = p.Warnings,
        error = p.Error,
        created_at = p.CreatedAt,
        finished_at = p.FinishedAt,
    };

    private static object ToBatchDto(RefactorBatchInfo b) => new
    {
        id = b.Id,
        plan_id = b.PlanId,
        n = b.N,
        repository = b.Repository,
        files = b.Files,
        depends_on = b.DependsOn,
        description = b.Description,
        risk = b.Risk,
        verify_cmd = b.VerifyCommand,
        state = b.State,
        pr_url = b.PrUrl,
        pr_number = b.PrNumber,
        conversation_id = b.ConversationId,
        attempts = b.Attempts,
        verify_log_digest = b.VerifyLogDigest,
        error = b.Error,
        created_at = b.CreatedAt,
        updated_at = b.UpdatedAt,
    };
}