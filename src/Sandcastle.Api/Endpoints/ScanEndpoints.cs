using Microsoft.AspNetCore.Mvc;

using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Scans;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// SPEC-143 — code scans (<c>/api/v1/scans</c>,
/// <c>/api/v1/orgs/{org_id}/scans|scan-findings</c>,
/// <c>/api/v1/scan-findings/{id}*</c>). Permissões:
/// <c>perm:scans.run</c> (start/list/get), <c>perm:scans.findings</c>
/// (feed, triagem, fix), <c>perm:scans.manage</c> reservado p/ schedules.
/// Escopo = org efetiva (X-Org-Id) ou <c>{org_id}</c> da rota.
/// </summary>
public static class ScanEndpoints
{
    private const string PermScansRun = "perm:scans.run";
    private const string PermScansFindings = "perm:scans.findings";
    private const string ScanNotFound = "scan not found";
    private const string FindingNotFound = "scan finding not found";

    /// <summary>Mapeia os endpoints de scans + findings.</summary>
    public static IEndpointRouteBuilder MapScanEndpoints(
        this IEndpointRouteBuilder app)
    {
        var scans = app.MapGroup("/api/v1/scans").WithTags("Scans");

        // POST /scans {repositories[], goal, profile?, effort?} → 202.
        scans.MapPost("", CreateScanAsync)
            .RequireAuthorization(PermScansRun);

        // GET /scans?status=&cursor=&limit= — lista org-scoped cursada.
        scans.MapGet("", ListScansAsync)
            .RequireAuthorization(PermScansRun);

        scans.MapGet("/{id:guid}", GetScanAsync)
            .RequireAuthorization(PermScansRun);

        // GET /scans/{id}/findings — RF-004 filter set + cursor.
        scans.MapGet("/{id:guid}/findings", GetScanFindingsAsync)
            .RequireAuthorization(PermScansFindings);

        // Devin v3 parity: org-wide feeds.
        app.MapGet("/api/v1/orgs/{org_id:guid}/scans", ListOrgScansAsync)
            .WithTags("Scans").RequireAuthorization(PermScansRun);
        app.MapGet("/api/v1/orgs/{org_id:guid}/scan-findings",
            SearchOrgFindingsAsync).WithTags("Scans")
            .RequireAuthorization(PermScansFindings);

        var findings = app.MapGroup("/api/v1/scan-findings")
            .WithTags("Scans");

        findings.MapGet("/{id:guid}", GetFindingAsync)
            .RequireAuthorization(PermScansFindings);

        // PATCH {status} — triagem open|reviewed|dismissed.
        findings.MapPatch("/{id:guid}", PatchFindingAsync)
            .RequireAuthorization(PermScansFindings);

        // "Fix with Sandcastle" — ação explícita, nunca automática.
        findings.MapPost("/{id:guid}/fix", FixFindingAsync)
            .RequireAuthorization(PermScansFindings);

        findings.MapPost("/fix-bulk", FixBulkAsync)
            .RequireAuthorization(PermScansFindings);

        return app;
    }

    private static async Task<IResult> CreateScanAsync(
        [FromBody] CreateScanRequest body, HttpContext http,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        IScanService scans, CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var org = await orgs.ResolveAsync(
            u, http.Request.Headers["X-Org-Id"].ToString(), ct);
        try
        {
            var (created, error) = await scans.CreateAsync(
                body, u.UserId, org?.OrgId, ScanTriggers.Manual, ct);
            return created is null
                ? SandcastleProblemFactory.Create(
                    http, 400, error ?? "invalid scan request")
                : Results.Accepted(
                    $"/api/v1/scans/{created.ScanId}", new
                    {
                        scan_id = created.ScanId,
                        conversation_id = created.CoordinatorConversationId,
                        coordinator_conversation_id =
                            created.CoordinatorConversationId,
                        status = created.Status,
                    });
        }
        catch (Application.Identity.PlanLimitExceededException ex)
        {
            // SPEC-143: quota/concurrent cap → 403 com limit_key.
            return AppConversationEndpoints.PlanLimitProblem(ex.Error);
        }
    }

    private static async Task<IResult> ListScansAsync(
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IScanService scans,
        [FromQuery] string? status, [FromQuery] string? cursor,
        [FromQuery] int? limit, CancellationToken ct) =>
        await ListScansCoreAsync(http, current, orgs, scans,
            null, status, cursor, limit, ct);

    private static async Task<IResult> ListOrgScansAsync(
        Guid org_id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IScanService scans,
        [FromQuery] string? status, [FromQuery] string? cursor,
        [FromQuery] int? limit, CancellationToken ct) =>
        await ListScansCoreAsync(http, current, orgs, scans,
            org_id, status, cursor, limit, ct);

    private static async Task<IResult> ListScansCoreAsync(
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IScanService scans,
        Guid? routeOrg, string? status, string? cursor,
        int? limit, CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var orgId = await ResolveOrgAsync(orgs, u, routeOrg, http, ct);
        if (orgId is null)
        {
            return SandcastleProblemFactory.Create(http, 404, "no org");
        }

        var page = await scans.ListAsync(
            orgId.Value, status, cursor, limit ?? 50, ct);
        return Results.Ok(new
        {
            items = page.Items.Select(ToScanDto).ToList(),
            next_page_id = page.NextPageId,
        });
    }

    private static async Task<IResult> GetScanAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IScanService scans,
        CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var orgId = await ResolveOrgAsync(orgs, u, null, http, ct);
        if (orgId is null)
        {
            return SandcastleProblemFactory.Create(http, 404, "no org");
        }

        var scan = await scans.GetAsync(orgId.Value, id, ct);
        return scan is null
            ? SandcastleProblemFactory.Create(http, 404, ScanNotFound)
            : Results.Ok(ToScanDto(scan));
    }

    private static async Task<IResult> GetScanFindingsAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IScanService scans,
        [FromQuery] string? repository, [FromQuery] string? severity,
        [FromQuery] string? status, [FromQuery] string? kind,
        [FromQuery] string? remediation_state, [FromQuery] string? q,
        [FromQuery] string? after, [FromQuery] int? first,
        CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var orgId = await ResolveOrgAsync(orgs, u, null, http, ct);
        if (orgId is null)
        {
            return SandcastleProblemFactory.Create(http, 404, "no org");
        }

        // 404 quando o scan não existe na org — o feed não vaza órfãos.
        if (await scans.GetAsync(orgId.Value, id, ct) is null)
        {
            return SandcastleProblemFactory.Create(http, 404, ScanNotFound);
        }

        var page = await scans.SearchFindingsAsync(orgId.Value,
            new ScanFindingQuery(id, repository, severity, status, kind,
                remediation_state, q, after, first), ct);
        return Results.Ok(ToFindingPageDto(page));
    }

    private static async Task<IResult> SearchOrgFindingsAsync(
        Guid org_id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IScanService scans,
        [FromQuery] Guid? scan_id, [FromQuery] string? repository,
        [FromQuery] string? severity, [FromQuery] string? status,
        [FromQuery] string? kind, [FromQuery] string? remediation_state,
        [FromQuery] string? q, [FromQuery] string? after,
        [FromQuery] int? first, CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        if (!await orgs.IsMemberAsync(u.UserId, org_id, ct))
        {
            return SandcastleProblemFactory.Create(http, 404, "no org");
        }

        var page = await scans.SearchFindingsAsync(org_id,
            new ScanFindingQuery(scan_id, repository, severity, status,
                kind, remediation_state, q, after, first), ct);
        return Results.Ok(ToFindingPageDto(page));
    }

    private static async Task<IResult> GetFindingAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IScanService scans,
        CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var orgId = await ResolveOrgAsync(orgs, u, null, http, ct);
        if (orgId is null)
        {
            return SandcastleProblemFactory.Create(http, 404, "no org");
        }

        var f = await scans.GetFindingAsync(orgId.Value, id, ct);
        return f is null
            ? SandcastleProblemFactory.Create(http, 404, FindingNotFound)
            : Results.Ok(ToFindingDto(f));
    }

    private static async Task<IResult> PatchFindingAsync(
        Guid id, [FromBody] PatchScanFindingRequest body,
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IScanService scans,
        CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }
        if (string.IsNullOrWhiteSpace(body.Status))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "status required");
        }

        var orgId = await ResolveOrgAsync(orgs, u, null, http, ct);
        if (orgId is null)
        {
            return SandcastleProblemFactory.Create(http, 404, "no org");
        }

        try
        {
            var f = await scans.UpdateFindingStatusAsync(
                orgId.Value, id, body.Status, ct);
            return f is null
                ? SandcastleProblemFactory.Create(
                    http, 404, FindingNotFound)
                : Results.Ok(ToFindingDto(f));
        }
        catch (ArgumentException e)
        {
            return SandcastleProblemFactory.Create(http, 400, e.Message);
        }
    }

    private static async Task<IResult> FixFindingAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IScanService scans,
        CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        var orgId = await ResolveOrgAsync(orgs, u, null, http, ct);
        if (orgId is null)
        {
            return SandcastleProblemFactory.Create(http, 404, "no org");
        }

        try
        {
            var r = await scans.FixAsync(orgId.Value, id, u.UserId, ct);
            return ToFixResult(http, id, r);
        }
        catch (Application.Identity.PlanLimitExceededException ex)
        {
            return AppConversationEndpoints.PlanLimitProblem(ex.Error);
        }
    }

    private static async Task<IResult> FixBulkAsync(
        [FromBody] FixScanFindingsBulkRequest body, HttpContext http,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        IScanService scans, CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }
        if (body.Ids is not { Count: > 0 } ids)
        {
            return SandcastleProblemFactory.Create(
                http, 400, "ids required");
        }

        var orgId = await ResolveOrgAsync(orgs, u, null, http, ct);
        if (orgId is null)
        {
            return SandcastleProblemFactory.Create(http, 404, "no org");
        }

        IReadOnlyList<ScanFixResult> results;
        try
        {
            results = await scans.FixBulkAsync(
                orgId.Value, ids, u.UserId, ct);
        }
        catch (Application.Identity.PlanLimitExceededException ex)
        {
            return AppConversationEndpoints.PlanLimitProblem(ex.Error);
        }
        return Results.Accepted(null, new
        {
            items = results.Select(r => new
            {
                finding_id = r.FindingId,
                conversation_id = r.ConversationId,
                error = r.Error,
            }).ToList(),
        });
    }

    private static IResult ToFixResult(
        HttpContext http, Guid id, ScanFixResult r) =>
        r.Error switch
        {
            null => Results.Accepted(null, new
            {
                finding_id = id,
                conversation_id = r.ConversationId,
            }),
            "not_found" => SandcastleProblemFactory.Create(
                http, 404, FindingNotFound),
            "fix_in_progress" => SandcastleProblemFactory.Create(
                http, 409, "fix already in progress"),
            _ => SandcastleProblemFactory.Create(http, 400, r.Error),
        };

    /// <summary>
    /// Org efetiva: route {org_id} (com membership check) &gt; header
    /// X-Org-Id &gt; org default do usuário.
    /// </summary>
    private static async Task<Guid?> ResolveOrgAsync(
        IEffectiveOrgResolver orgs, AuthUser u, Guid? routeOrg,
        HttpContext http, CancellationToken ct)
    {
        if (routeOrg is { } rid)
        {
            return await orgs.IsMemberAsync(u.UserId, rid, ct)
                ? rid : null;
        }

        return (await orgs.ResolveAsync(
            u, http.Request.Headers["X-Org-Id"].ToString(), ct))?.OrgId;
    }

    private static object ToScanDto(ScanSummary s) => new
    {
        id = s.Id,
        org_id = s.OrgId,
        repositories = s.Repositories,
        goal = s.Goal,
        profile = s.Profile,
        effort = s.Effort,
        status = s.Status,
        trigger = s.Trigger,
        coordinator_conversation_id = s.CoordinatorConversationId,
        findings_count = s.FindingsCount,
        severity_counts = s.SeverityCounts,
        error = s.Error,
        created_at = s.CreatedAt,
        finished_at = s.FinishedAt,
    };

    private static object ToFindingPageDto(ScanFindingPage p) => new
    {
        items = p.Items.Select(ToFindingDto).ToList(),
        next_page_id = p.NextPageId,
    };

    private static object ToFindingDto(ScanFindingInfo f) => new
    {
        id = f.Id,
        scan_id = f.ScanId,
        org_id = f.OrgId,
        repository = f.Repository,
        path = f.Path,
        start_line = f.StartLine,
        end_line = f.EndLine,
        kind = f.Kind,
        severity = f.Severity,
        confidence = f.Confidence,
        title = f.Title,
        description = f.Description,
        remediation_hint = f.RemediationHint,
        status = f.Status,
        remediation_state = f.RemediationState,
        conversation_id = f.ConversationId,
        pr_url = f.PrUrl,
        payoff = f.Payoff,
        created_at = f.CreatedAt,
        updated_at = f.UpdatedAt,
    };
}

/// <summary>Body do PATCH de triagem.</summary>
public sealed record PatchScanFindingRequest(string? Status);

/// <summary>Body do fix-bulk {ids[]}.</summary>
public sealed record FixScanFindingsBulkRequest(
    IReadOnlyList<Guid>? Ids);