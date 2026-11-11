using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Scm;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// <c>/api/v1/scm</c> + <c>/api/v1/orgs/{org}/scm-connections</c> +
/// <c>/api/v1/webhooks/scm/github</c> + <c>/app-conversations/{id}/pull-request</c>
/// (SPEC-52 — GitHub App, webhooks verificados, PR do workspace).
/// </summary>
public static class ScmEndpoints
{
    private const string PermManageIntegrations = "perm:manage_integrations";
    private const int MaxWebhookPayload = 256 * 1024;

    /// <summary>Maps SCM endpoints.</summary>
    public static IEndpointRouteBuilder MapScmEndpoints(
        this IEndpointRouteBuilder app)
    {
        var scm = app.MapGroup("/api/v1/scm").WithTags("SCM");

        // Manifest flow — devolve o manifest JSON p/ criar o GitHub App.
        scm.MapPost("/github/manifest",
            PostGithubManifestAsync).RequireAuthorization();

        // Callback do manifest flow — troca code → credenciais do app.
        scm.MapGet("/github/callback", GetGithubCallbackAsync).RequireAuthorization(PermManageIntegrations);

        // Pull request a partir do workspace da conversa.
        app.MapPost("/api/v1/app-conversations/{id:guid}/pull-request",
            CreatePullRequestAsync).WithTags(EndpointTags.Api)
            .RequireAuthorization();

        // Admin: connections CRUD.
        var conns = app.MapGroup("/api/v1/orgs/{org_id:guid}/scm-connections")
            .WithTags("SCM");
        conns.MapGet("", ListConnectionsAsync)
            .RequireAuthorization(PermManageIntegrations);

        conns.MapPost("", CreateConnectionAsync)
            .RequireAuthorization(PermManageIntegrations);

        // SPEC-53 RF-004: diagnóstico da connection (token + escopo).
        scm.MapPost("/connections/{id:guid}/test", PostConnectionsIdGuidTestAsync).RequireAuthorization(PermManageIntegrations);

        // SPEC-140 RF-001: enrollment de auto-review na connection.
        scm.MapPatch("/connections/{id:guid}", PatchConnectionReviewAsync)
            .RequireAuthorization(PermManageIntegrations);

        conns.MapDelete("/{id:guid}", DeleteIdGuidAsync).RequireAuthorization(PermManageIntegrations);

        // SPEC-140: auto-review — trigger manual + listagem/detalle/PATCH.
        scm.MapPost("/reviews", PostReviewAsync).RequireAuthorization();
        scm.MapGet("/reviews", ListReviewsAsync).RequireAuthorization();
        scm.MapGet("/reviews/{id:guid}", GetReviewAsync).RequireAuthorization();
        scm.MapPatch("/reviews/{id:guid}/findings/{fid:guid}",
            PatchFindingAsync).RequireAuthorization();

        // SPEC-141: CI autofix — trigger manual + runs (list/detail/cancel).
        scm.MapPost("/autofix", PostAutofixAsync).RequireAuthorization();
        scm.MapGet("/autofix-runs", ListAutofixRunsAsync)
            .RequireAuthorization();
        scm.MapGet("/autofix-runs/{id:guid}", GetAutofixRunAsync)
            .RequireAuthorization();
        scm.MapPost("/autofix-runs/{id:guid}/cancel", CancelAutofixRunAsync)
            .RequireAuthorization();

        // SPEC-142: QA agent — trigger manual + runs (list/detail) +
        // evidência auth'd (artifacts do run — ACL da org).
        var qa = app.MapGroup("/api/v1/qa").WithTags("QA");
        qa.MapPost("/runs", PostQaRunAsync).RequireAuthorization();
        qa.MapGet("/runs", ListQaRunsAsync).RequireAuthorization();
        qa.MapGet("/runs/{id:guid}", GetQaRunAsync).RequireAuthorization();
        qa.MapGet("/runs/{id:guid}/artifacts/{*ref}",
            GetQaArtifactAsync).RequireAuthorization();

        // Webhook público (HMAC + idempotência; sem auth de usuário).
        app.MapPost("/api/v1/webhooks/scm/{kind}",
            HandleScmWebhookAsync).WithTags(EndpointTags.Api);

        return app;
    }

    // ---- SPEC-142: QA agent endpoints -------------------------------------

    /// <summary><c>POST /qa/runs</c> — trigger manual {repository, pr_number} → 202.</summary>
    private static async Task<IResult> PostQaRunAsync( // NOSONAR S107 — minimal-API handler: bindings individuais
        [FromBody] ScmQaRunRequest req, HttpContext http,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        IScmConnectionService connections, IQaService qaService,
        Microsoft.Extensions.Options.IOptions<
            Infrastructure.Scm.Qa.QaOptions> qaOptions,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (!qaOptions.Value.Enabled)
        {
            return SandcastleProblemFactory.Create(
                http, 403, "qa agent is disabled");
        }
        if (string.IsNullOrWhiteSpace(req.Repository) || req.PrNumber <= 0)
        {
            return SandcastleProblemFactory.Create(
                http, 400, "repository and pr_number are required");
        }
        var org = await orgs.ResolveAsync(
            user, http.Request.Headers["X-Org-Id"].ToString(), ct);
        if (org is null)
        {
            return SandcastleProblemFactory.Create(http, 404, "no org");
        }
        var connection = (await connections.ListAsync(org.OrgId, ct))
            .FirstOrDefault(c => c.Repos.Count == 0
                || c.Repos.Contains(req.Repository,
                    StringComparer.OrdinalIgnoreCase));
        if (connection is null)
        {
            return SandcastleProblemFactory.Create(
                http, 404, "no scm connection for repository");
        }

        var result = await qaService.TriggerManualAsync(
            connection, req.Repository, req.PrNumber,
            user.UserId.ToString(), ct);
        return result switch
        {
            { QaRunId: { } id } => Results.Accepted(
                $"/api/v1/qa/runs/{id}", new { qa_run_id = id }),
            { Error: QaTriggerError.NotFound } =>
                SandcastleProblemFactory.Create(
                    http, 404, "pull request not found"),
            { Error: QaTriggerError.Disabled } =>
                SandcastleProblemFactory.Create(http, 403,
                    "qa agent not enabled for this repository"),
            { Error: QaTriggerError.Duplicate, Detail: var d } =>
                SandcastleProblemFactory.Create(http, 409,
                    d ?? "qa run already covers this head"),
            { Error: QaTriggerError.QuotaExceeded, Detail: var d } =>
                SandcastleProblemFactory.Create(http, 403,
                    d ?? "daily qa quota reached"),
            _ => SandcastleProblemFactory.Create(
                http, 409, "qa run could not be started"),
        };
    }

    /// <summary><c>GET /qa/runs</c> — filtros repository/pr/verdict + cursor.</summary>
    private static async Task<IResult> ListQaRunsAsync( // NOSONAR S107 — minimal-API handler: filtros query bound
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IQaService qaService,
        string? repository, int? pr, string? verdict,
        string? page_id, int? limit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var org = await orgs.ResolveAsync(
            user, http.Request.Headers["X-Org-Id"].ToString(), ct);
        if (org is null)
        {
            return Results.Ok(new
            {
                items = Array.Empty<object>(),
                next_page_id = (string?)null
            });
        }
        var page = await qaService.SearchAsync(
            org.OrgId, repository, pr, verdict, page_id,
            limit ?? 50, ct);
        return Results.Ok(new
        {
            items = page.Items,
            next_page_id = page.NextPageId,
        });
    }

    /// <summary><c>GET /qa/runs/{id}</c> — run + plano + passos + evidência.</summary>
    private static async Task<IResult> GetQaRunAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IQaService qaService,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var org = await orgs.ResolveAsync(
            user, http.Request.Headers["X-Org-Id"].ToString(), ct);
        if (org is null)
        {
            return Results.NotFound();
        }
        var detail = await qaService.GetAsync(org.OrgId, id, ct);
        return detail is null
            ? Results.NotFound()
            : Results.Ok(new
            {
                run = detail.Run,
                plan = detail.Plan,
                steps = detail.Steps,
                evidence = detail.Evidence,
            });
    }

    /// <summary>
    /// <c>GET /qa/runs/{id}/artifacts/{*ref}</c> — evidência auth'd do run
    /// (screenshot/vídeo). ACL: org do run; o namespace do artifact é a
    /// conversa do PR quando existe, senão o próprio run id.
    /// </summary>
    private static async Task<IResult> GetQaArtifactAsync( // NOSONAR S107 — minimal-API handler: route/services bound
        Guid id, string @ref, HttpContext http,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        IQaService qaService,
        Application.Artifacts.IArtifactStore store,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var org = await orgs.ResolveAsync(
            user, http.Request.Headers["X-Org-Id"].ToString(), ct);
        if (org is null)
        {
            return Results.NotFound();
        }
        var detail = await qaService.GetAsync(org.OrgId, id, ct);
        if (detail is null)
        {
            return Results.NotFound();
        }
        // Ref deve pertencer ao run (defesa contra ref arbitrária de
        // outro namespace com mesmo id — a loja é keyed por namespace).
        if (!detail.Steps.Any(s => s.ArtifactRef == @ref))
        {
            return Results.NotFound();
        }
        var stream = await store.OpenAsync(
            detail.Run.ConversationId ?? detail.Run.Id, @ref, ct);
        if (stream is null)
        {
            return Results.NotFound();
        }
        var contentType = @ref.EndsWith(".webm",
                StringComparison.OrdinalIgnoreCase)
            || detail.Steps.Any(s => s.ArtifactRef == @ref
                && s.Action == "video")
            ? "video/webm" : "image/png";
        return Results.File(stream, contentType,
            enableRangeProcessing: true);
    }

    // ---- SPEC-140: review endpoints ---------------------------------------

    private static async Task<IResult> PostReviewAsync(
        [FromBody] ScmReviewRequest req, HttpContext http,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        IScmConnectionService connections, IScmProviderRegistry providers,
        IReviewService reviews,
        Microsoft.Extensions.Options.IOptions<
            Infrastructure.Scm.Review.ReviewOptions> reviewOptions,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (!reviewOptions.Value.Enabled)
        {
            return SandcastleProblemFactory.Create(
                http, 403, "auto-review is disabled");
        }
        if (string.IsNullOrWhiteSpace(req.Repository) || req.PrNumber <= 0)
        {
            return SandcastleProblemFactory.Create(
                http, 400, "repository and pr_number are required");
        }
        var org = await orgs.ResolveAsync(
            user, http.Request.Headers["X-Org-Id"].ToString(), ct);
        if (org is null)
        {
            return SandcastleProblemFactory.Create(http, 404, "no org");
        }

        // Opt-in por conexão/repo também no trigger manual (RF-001 —
        // Disabled = 403); connection do repo = primeiro match.
        var connection = (await connections.ListAsync(org.OrgId, ct))
            .FirstOrDefault(c => c.Repos.Count == 0
                || c.Repos.Contains(req.Repository,
                    StringComparer.OrdinalIgnoreCase));
        if (connection is null)
        {
            return SandcastleProblemFactory.Create(
                http, 404, "no scm connection for repository");
        }
        if (!connection.ReviewCovers(req.Repository))
        {
            return SandcastleProblemFactory.Create(http, 403,
                "auto-review not enabled for this repository");
        }
        var provider = providers.Get(connection.Kind);
        if (provider is null)
        {
            return SandcastleProblemFactory.Create(
                http, 502, "scm provider not registered");
        }

        ScmPullRequest? pr;
        try
        {
            pr = await provider.GetPullRequestAsync(
                connection, req.Repository, req.PrNumber, ct);
        }
        catch (Exception)
        {
            pr = null;
        }
        if (pr is null)
        {
            return SandcastleProblemFactory.Create(
                http, 404, "pull request not found");
        }

        var result = await reviews.TriggerAsync(
            connection, req.Repository, req.PrNumber, pr.HeadSha,
            "manual", user.UserId.ToString(), ct);
        return result switch
        {
            { ReviewRunId: { } id } => Results.Accepted(
                $"/api/v1/scm/reviews/{id}", new { review_run_id = id }),
            { Error: ReviewTriggerError.Duplicate, Detail: var d } =>
                SandcastleProblemFactory.Create(http, 409,
                    d ?? "review already in progress for this head"),
            { Error: ReviewTriggerError.QuotaExceeded, Detail: var d } =>
                SandcastleProblemFactory.Create(http, 403,
                    d ?? "daily review quota reached"),
            _ => SandcastleProblemFactory.Create(
                http, 409, "review could not be started"),
        };
    }

    private static async Task<IResult> ListReviewsAsync(
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IReviewService reviews,
        string? repository, int? pr, string? status,
        string? page_id, int? limit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var org = await orgs.ResolveAsync(
            user, http.Request.Headers["X-Org-Id"].ToString(), ct);
        if (org is null)
        {
            return Results.Ok(new
            {
                items = Array.Empty<object>(),
                next_page_id = (string?)null
            });
        }
        var page = await reviews.SearchAsync(
            org.OrgId, repository, pr, status, page_id, limit ?? 50, ct);
        return Results.Ok(new
        {
            items = page.Items,
            next_page_id = page.NextPageId,
        });
    }

    private static async Task<IResult> GetReviewAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IReviewService reviews,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var org = await orgs.ResolveAsync(
            user, http.Request.Headers["X-Org-Id"].ToString(), ct);
        if (org is null)
        {
            return Results.NotFound();
        }
        var detail = await reviews.GetAsync(org.OrgId, id, ct);
        return detail is null
            ? Results.NotFound()
            : Results.Ok(new { run = detail.Run, findings = detail.Findings });
    }

    private static async Task<IResult> PatchFindingAsync(
        Guid id, Guid fid, [FromBody] ScmFindingPatchRequest req,
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IReviewService reviews,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var org = await orgs.ResolveAsync(
            user, http.Request.Headers["X-Org-Id"].ToString(), ct);
        if (org is null)
        {
            return Results.NotFound();
        }
        if (req.Status is null
            || !ReviewFindingStatuses.Updatable.Contains(req.Status))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "status must be open|dismissed");
        }
        var finding = await reviews.UpdateFindingStatusAsync(
            org.OrgId, id, fid, req.Status, ct);
        return finding is null
            ? Results.NotFound()
            : Results.Ok(finding);
    }

    private static async Task<IResult> PatchConnectionReviewAsync(
        Guid id, [FromBody] ScmConnectionReviewPatch req, HttpContext http,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        IScmConnectionService svc, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }
        // Effective org (X-Org-Id membership-checked → bound → default) —
        // UpdateReviewSettings valida que a connection pertence à org.
        var org = await orgs.ResolveAsync(
            user, http.Request.Headers["X-Org-Id"].ToString(), ct);
        if (org is null)
        {
            return Results.NotFound();
        }
        if (ValidateReviewPatch(req, http) is { } bad)
        {
            return bad;
        }
        var conn = await svc.UpdateReviewSettingsAsync(
            org.OrgId, id,
            new ScmConnectionReviewUpdate(
                req.ReviewEnabled, req.ReviewRepos,
                req.ReviewTriggerMode, req.ReviewPassCondition), ct);
        if (conn is null)
        {
            return Results.NotFound();
        }
        // SPEC-141 RF-005: campos autofix_* na mesma PATCH (qualquer
        // campo autofix presente → update de settings dedicado).
        if (req.AutofixEnabled is not null || req.AutofixRepos is not null
            || req.AutofixCheckPatterns is not null
            || req.AutofixMaxAttempts is not null)
        {
            conn = await svc.UpdateAutofixSettingsAsync(
                org.OrgId, id,
                new ScmConnectionAutofixUpdate(
                    req.AutofixEnabled, req.AutofixRepos,
                    req.AutofixCheckPatterns, req.AutofixMaxAttempts), ct);
        }
        // SPEC-142 RF-001: campos qa_* na mesma PATCH.
        if (req.QaEnabled is not null || req.QaRepos is not null
            || req.QaPassCondition is not null)
        {
            conn = await svc.UpdateQaSettingsAsync(
                org.OrgId, id,
                new ScmConnectionQaUpdate(
                    req.QaEnabled, req.QaRepos, req.QaPassCondition), ct);
        }
        return conn is null ? Results.NotFound() : Results.Ok(conn);
    }

    /// <summary>
    /// Valida os campos enumerados do patch de settings SCM; retorna o
    /// ProblemDetails ou <c>null</c> quando válido.
    /// </summary>
    private static IResult? ValidateReviewPatch(
        ScmConnectionReviewPatch req, HttpContext http)
    {
        if (req.ReviewTriggerMode is { } m
            && m is not (ReviewDefaults.TriggerModeAll
                or ReviewDefaults.TriggerModeLabelOnly))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "review_trigger_mode must be all_prs|label_only");
        }
        if (req.ReviewPassCondition is { } p
            && p is not (ReviewDefaults.PassConditionCompleted
                or ReviewDefaults.PassConditionNoCritical
                or ReviewDefaults.PassConditionNoFindings))
        {
            return SandcastleProblemFactory.Create(http, 400,
                "review_pass_condition must be "
                + "completed|no_critical|no_findings");
        }
        if (req.AutofixMaxAttempts is < 1 or > 10)
        {
            return SandcastleProblemFactory.Create(
                http, 400, "autofix_max_attempts must be 1..10");
        }
        if (req.QaPassCondition is { } qc
            && qc is not (QaDefaults.PassConditionCompleted
                or QaDefaults.PassConditionPassed))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "qa_pass_condition must be completed|passed");
        }
        return null;
    }

    // ---- SPEC-141: autofix endpoints --------------------------------------

    /// <summary><c>POST /scm/autofix</c> — trigger manual {repository, pr_number}.</summary>
    private static async Task<IResult> PostAutofixAsync(
        [FromBody] ScmAutofixRequest req, HttpContext http,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        IScmConnectionService connections, IAutofixService autofix,
        Microsoft.Extensions.Options.IOptions<
            Infrastructure.Scm.Autofix.AutofixOptions> autofixOptions,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (!autofixOptions.Value.Enabled)
        {
            return SandcastleProblemFactory.Create(
                http, 403, "ci autofix is disabled");
        }
        if (string.IsNullOrWhiteSpace(req.Repository) || req.PrNumber <= 0)
        {
            return SandcastleProblemFactory.Create(
                http, 400, "repository and pr_number are required");
        }
        var org = await orgs.ResolveAsync(
            user, http.Request.Headers["X-Org-Id"].ToString(), ct);
        if (org is null)
        {
            return SandcastleProblemFactory.Create(http, 404, "no org");
        }

        var connection = (await connections.ListAsync(org.OrgId, ct))
            .FirstOrDefault(c => c.Repos.Count == 0
                || c.Repos.Contains(req.Repository,
                    StringComparer.OrdinalIgnoreCase));
        if (connection is null)
        {
            return SandcastleProblemFactory.Create(
                http, 404, "no scm connection for repository");
        }

        var result = await autofix.TriggerManualAsync(
            connection, req.Repository, req.PrNumber,
            user.UserId.ToString(), ct);
        return result switch
        {
            { RunId: { } id } => Results.Accepted(
                $"/api/v1/scm/autofix-runs/{id}",
                new { autofix_run_id = id }),
            { Error: AutofixTriggerError.NotFound, Detail: var d } =>
                SandcastleProblemFactory.Create(
                    http, 404, d ?? "pull request not found"),
            { Error: AutofixTriggerError.NoConnection, Detail: var d } =>
                SandcastleProblemFactory.Create(
                    http, 404, d ?? "no scm connection for repository"),
            { Error: AutofixTriggerError.Disabled, Detail: var d } =>
                SandcastleProblemFactory.Create(http, 403,
                    d ?? "autofix not enabled for this repository"),
            { Error: AutofixTriggerError.Duplicate, Detail: var d } =>
                SandcastleProblemFactory.Create(http, 409,
                    d ?? "autofix run already active for this pr"),
            _ => SandcastleProblemFactory.Create(
                http, 409, "autofix could not be started"),
        };
    }

    private static async Task<IResult> ListAutofixRunsAsync(
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IAutofixService autofix,
        string? repository, int? pr, string? status,
        string? page_id, int? limit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var org = await orgs.ResolveAsync(
            user, http.Request.Headers["X-Org-Id"].ToString(), ct);
        if (org is null)
        {
            return Results.Ok(new
            {
                items = Array.Empty<object>(),
                next_page_id = (string?)null
            });
        }
        var page = await autofix.SearchAsync(
            org.OrgId, repository, pr, status, page_id, limit ?? 50, ct);
        return Results.Ok(new
        {
            items = page.Items,
            next_page_id = page.NextPageId,
        });
    }

    private static async Task<IResult> GetAutofixRunAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IAutofixService autofix,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var org = await orgs.ResolveAsync(
            user, http.Request.Headers["X-Org-Id"].ToString(), ct);
        if (org is null)
        {
            return Results.NotFound();
        }
        var detail = await autofix.GetAsync(org.OrgId, id, ct);
        return detail is null
            ? Results.NotFound()
            : Results.Ok(new { run = detail.Run, attempts = detail.Attempts });
    }

    private static async Task<IResult> CancelAutofixRunAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IAutofixService autofix,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var org = await orgs.ResolveAsync(
            user, http.Request.Headers["X-Org-Id"].ToString(), ct);
        if (org is null)
        {
            return Results.NotFound();
        }
        return await autofix.CancelAsync(org.OrgId, id, ct)
            ? Results.Ok(new { status = AutofixRunStatuses.Cancelled })
            : Results.NotFound();
    }

    private static async Task<IResult> CreatePullRequestAsync(
        Guid id, [FromBody] ScmPrRequest req,
        ICurrentUserAccessor current, IScmPullRequestService svc,
        HttpContext http, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        var result = await svc.CreateAsync(id,
            new ScmPrCreateRequest(
                req.Title, req.Body, req.Base,
                req.Head ?? "", req.Draft ?? false, req.Reviewers,
                req.Repository),
            user.UserId.ToString(), ct);
        return (result.Error, result.PullRequest) switch
        {
            (ScmPrError.NotFound, _) =>
                SandcastleProblemFactory.Create(
                    http, 404, "conversation not found"),
            (ScmPrError.NoConnection, _) =>
                SandcastleProblemFactory.Create(
                    http, 404, "no scm connection for repository"),
            (ScmPrError.InvalidRepository, _) =>
                SandcastleProblemFactory.Create(
                    http, 400, "repository is not in the conversation's selection"),
            (ScmPrError.Forbidden, _) =>
                SandcastleProblemFactory.Create(
                    http, 403, "no access to that repository"),
            (ScmPrError.AlreadyExists, _) =>
                SandcastleProblemFactory.Create(
                    http, 409, "pull request already exists for repository"),
            (ScmPrError.ProviderError, _) =>
                SandcastleProblemFactory.Create(
                    http, 502, "scm provider error"),
            (_, { } pr) => Results.Ok(new
            {
                url = pr.Url,
                number = pr.Number,
                state = pr.State,
            }),
            _ => SandcastleProblemFactory.Create(
                http, 500, "unexpected state"),
        };
    }

    private static async Task<IResult> HandleScmWebhookAsync(
        string kind, HttpRequest req, IScmWebhookService svc,
        CancellationToken ct)
    {
        var parsed = ScmKindExtensions.Parse(kind);
        if (parsed is null)
            return Results.NotFound(new { error = "unknown_kind" });
        if ((req.ContentLength ?? 0) > MaxWebhookPayload)
            return Results.StatusCode(413);
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms, ct);
        var payload = ms.ToArray();

        string? token, eventType, delivery;
        switch (parsed.Value)
        {
            case ScmKind.GitLab:
                token = req.Headers["X-Gitlab-Token"].FirstOrDefault();
                eventType = req.Headers["X-Gitlab-Event"]
                    .FirstOrDefault();
                delivery = req.Headers["X-Gitlab-Webhook-UUID"]
                    .FirstOrDefault();
                break;
            case ScmKind.Bitbucket:
                token = req.Headers["X-Hub-Signature"].FirstOrDefault();
                eventType = req.Headers["X-Event-Key"].FirstOrDefault();
                delivery = req.Headers["X-Request-UUID"]
                    .FirstOrDefault();
                break;
            case ScmKind.AzureDevOps:
                token = req.Headers["X-Sandcastle-Secret"]
                    .FirstOrDefault()
                    ?? req.Query["secret"].FirstOrDefault();
                try
                {
                    using var peek = JsonDocument.Parse(payload);
                    eventType = peek.RootElement
                        .TryGetProperty("eventType", out var et)
                            ? et.GetString() : null;
                    delivery = null;
                    if (peek.RootElement
                        .TryGetProperty("notificationId", out var nd)
                        && nd.ValueKind == JsonValueKind.Number)
                    {
                        delivery = nd.GetInt32().ToString();
                    }
                    else if (peek.RootElement.TryGetProperty(
                        "id", out var pid))
                    {
                        delivery = pid.ToString();
                    }
                }
                catch (JsonException)
                {
                    return Results.Unauthorized();
                }
                break;
            default: // github
                token = req.Headers["X-Hub-Signature-256"]
                    .FirstOrDefault();
                eventType = req.Headers["X-GitHub-Event"]
                    .FirstOrDefault();
                delivery = req.Headers["X-GitHub-Delivery"]
                    .FirstOrDefault();
                break;
        }

        var result = await svc.HandleAsync(
            parsed.Value, token, eventType, delivery, payload, ct);
        return result switch
        {
            ScmWebhookResult.InvalidSignature =>
                Results.Unauthorized(),
            ScmWebhookResult.NoConnection =>
                Results.NotFound(new { error = "no_connection" }),
            ScmWebhookResult.Duplicate =>
                Results.Ok(new { status = "duplicate" }),
            ScmWebhookResult.Ignored =>
                Results.Ok(new { status = "ignored" }),
            _ => Results.Ok(new { status = "processed" }),
        };
    }


    private static async Task<IResult> GetGithubCallbackAsync([FromQuery] string code, [FromQuery] Guid org_id, ICurrentUserAccessor current, IScmConnectionService svc, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        var conn = await svc.CompleteGitHubManifestFlowAsync(
            org_id, code, user.UserId.ToString(), ct);
        return conn is null
            ? Results.BadRequest(new { error = "manifest_exchange_failed" })
            : Results.Ok(new
            {
                id = conn.Id,
                kind = conn.Kind.ToWire(),
                app_slug = conn.AppSlug,
            });

    }

    private static async Task<IResult> PostConnectionsIdGuidTestAsync(Guid id, ICurrentUserAccessor current, IScmConnectionService svc, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        var diag = await svc.TestAsync(id, ct);
        return Results.Ok(diag);

    }


    private static async Task<IResult> DeleteIdGuidAsync(Guid org_id, Guid id, ICurrentUserAccessor current, IScmConnectionService svc, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        return await svc.DeleteAsync(org_id, id, ct)
            ? Results.NoContent() : Results.NotFound();

    }

    private static IResult PostGithubManifestAsync(
        [FromBody] ScmManifestRequest req,
        IScmConnectionService svc, HttpContext http) =>
        !Uri.TryCreate(req.RedirectUri, UriKind.Absolute, out _)
            ? SandcastleProblemFactory.Create(
                http, 400, "redirect_uri must be an absolute URI")
            : Results.Ok(svc.BuildGitHubAppManifest(
                req.RedirectUri, req.AppName ?? "sandcastle"));

    private static async Task<IResult> ListConnectionsAsync(
        Guid org_id, ICurrentUserAccessor current,
        IScmConnectionService svc, CancellationToken ct)
    {
        var user = current.User;
        return user is null
            ? Results.Unauthorized()
            : Results.Ok(await svc.ListAsync(org_id, ct));
    }

    private static async Task<IResult> CreateConnectionAsync(
        Guid org_id, [FromBody] ScmConnectionRequest req,
        ICurrentUserAccessor current, IScmConnectionService svc,
        HttpContext http, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return Results.Unauthorized();
        var kind = ScmKindExtensions.Parse(req.Kind);
        if (kind is null)
        {
            return SandcastleProblemFactory.Create(
                http, 400, "unknown scm kind");
        }
        var conn = await svc.CreateAsync(
            org_id, kind.Value, req.InstallationId, req.PemSecretRef,
            req.WebhookSecretRef, req.Repos, req.AppId, req.AppSlug,
            user.UserId.ToString(), ct,
            req.ApiUrl, req.AuthKind, req.MetaJson);
        return Results.Ok(conn);
    }

}

/// <summary>Manifest-flow request.</summary>
public sealed record ScmManifestRequest(
    string RedirectUri, string? AppName);

/// <summary>
/// Pull-request request body. SPEC-146: <c>Repository</c> target —
/// null = primário da conversa.
/// </summary>
public sealed record ScmPrRequest(
    string Title,
    string Body,
    string? Base = null,
    string? Head = null,
    bool? Draft = null,
    IReadOnlyList<string>? Reviewers = null,
    string? Repository = null);

/// <summary>Manual connection registration (BYO app; SPEC-53 fields).</summary>
public sealed record ScmConnectionRequest(
    string Kind,
    string? InstallationId = null,
    string? PemSecretRef = null,
    string? WebhookSecretRef = null,
    IReadOnlyList<string>? Repos = null,
    string? AppId = null,
    string? AppSlug = null,
    string? ApiUrl = null,
    string? AuthKind = null,
    string? MetaJson = null);

/// <summary>SPEC-140: manual review trigger body.</summary>
public sealed record ScmReviewRequest(
    string? Repository = null,
    int PrNumber = 0);

/// <summary>SPEC-140: finding status patch (<c>open|dismissed</c>).</summary>
public sealed record ScmFindingPatchRequest(string? Status = null);

/// <summary>SPEC-140/141/142: review + autofix + qa enrollment patch on a connection.</summary>
public sealed record ScmConnectionReviewPatch(
    bool? ReviewEnabled = null,
    IReadOnlyList<string>? ReviewRepos = null,
    string? ReviewTriggerMode = null,
    string? ReviewPassCondition = null,
    bool? AutofixEnabled = null,
    IReadOnlyList<string>? AutofixRepos = null,
    IReadOnlyList<string>? AutofixCheckPatterns = null,
    int? AutofixMaxAttempts = null,
    bool? QaEnabled = null,
    IReadOnlyList<string>? QaRepos = null,
    string? QaPassCondition = null);

/// <summary>SPEC-142: manual QA trigger body.</summary>
public sealed record ScmQaRunRequest(
    string? Repository = null,
    int PrNumber = 0);

/// <summary>SPEC-141: manual autofix trigger body.</summary>
public sealed record ScmAutofixRequest(
    string? Repository = null,
    int PrNumber = 0);