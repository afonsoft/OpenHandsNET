using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Application.Scm;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// <c>/api/v1/scm</c> + <c>/api/v1/orgs/{org}/scm-connections</c> +
/// <c>/api/v1/webhooks/scm/github</c> + <c>/app-conversations/{id}/pull-request</c>
/// (SPEC-52 — GitHub App, webhooks verificados, PR do workspace).
/// </summary>
public static class ScmEndpoints
{
    private const int MaxWebhookPayload = 256 * 1024;

    /// <summary>Maps SCM endpoints.</summary>
    public static IEndpointRouteBuilder MapScmEndpoints(
        this IEndpointRouteBuilder app)
    {
        var scm = app.MapGroup("/api/v1/scm").WithTags("SCM");

        // Manifest flow — devolve o manifest JSON p/ criar o GitHub App.
        scm.MapPost("/github/manifest", (
            [FromBody] ScmManifestRequest req,
            IScmConnectionService svc) =>
            Results.Ok(svc.BuildGitHubAppManifest(
                req.RedirectUri, req.AppName ?? "openhandsnet")))
            .RequireAuthorization();

        // Callback do manifest flow — troca code → credenciais do app.
        scm.MapGet("/github/callback", async (
            [FromQuery] string code, [FromQuery] Guid org_id,
            ICurrentUserAccessor current, IScmConnectionService svc,
            CancellationToken ct) =>
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
                    id = conn.Id, kind = conn.Kind.ToWire(),
                    app_slug = conn.AppSlug,
                });
        }).RequireAuthorization("perm:manage_integrations");

        // Pull request a partir do workspace da conversa.
        app.MapPost("/api/v1/app-conversations/{id:guid}/pull-request",
            async (Guid id, [FromBody] ScmPrRequest req,
                ICurrentUserAccessor current, IScmPullRequestService svc,
                HttpContext http, CancellationToken ct) =>
            {
                var user = current.User;
                if (user is null)
                    return Results.Unauthorized();
                var result = await svc.CreateAsync(id,
                    new ScmPrCreateRequest(
                        req.Title, req.Body, req.Base,
                        req.Head ?? "", req.Draft ?? false, req.Reviewers),
                    user.UserId.ToString(), ct);
                return (result.Error, result.PullRequest) switch
                {
                    (ScmPrError.NotFound, _) =>
                        OpenHandsNetProblemFactory.Create(
                            http, 404, "conversation not found"),
                    (ScmPrError.NoConnection, _) =>
                        OpenHandsNetProblemFactory.Create(
                            http, 404, "no scm connection for repository"),
                    (ScmPrError.AlreadyExists, _) =>
                        OpenHandsNetProblemFactory.Create(
                            http, 409, "pull request already exists"),
                    (ScmPrError.ProviderError, _) =>
                        OpenHandsNetProblemFactory.Create(
                            http, 502, "scm provider error"),
                    (_, { } pr) => Results.Ok(new
                    {
                        url = pr.Url, number = pr.Number, state = pr.State,
                    }),
                    _ => OpenHandsNetProblemFactory.Create(
                        http, 500, "unexpected state"),
                };
            }).RequireAuthorization();

        // Admin: connections CRUD.
        var conns = app.MapGroup("/api/v1/orgs/{org_id:guid}/scm-connections")
            .WithTags("SCM");
        conns.MapGet("", async (
            Guid org_id, ICurrentUserAccessor current,
            IScmConnectionService svc, CancellationToken ct) =>
        {
            var user = current.User;
            return user is null
                ? Results.Unauthorized()
                : Results.Ok(await svc.ListAsync(org_id, ct));
        }).RequireAuthorization("perm:manage_integrations");

        conns.MapPost("", async (
            Guid org_id, [FromBody] ScmConnectionRequest req,
            ICurrentUserAccessor current, IScmConnectionService svc,
            HttpContext http, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            var kind = ScmKindExtensions.Parse(req.Kind);
            if (kind is null)
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 400, "unknown scm kind");
            }
            var conn = await svc.CreateAsync(
                org_id, kind.Value, req.InstallationId, req.PemSecretRef,
                req.WebhookSecretRef, req.Repos, req.AppId, req.AppSlug,
                user.UserId.ToString(), ct,
                req.ApiUrl, req.AuthKind, req.MetaJson);
            return Results.Ok(conn);
        }).RequireAuthorization("perm:manage_integrations");

        // SPEC-53 RF-004: diagnóstico da connection (token + escopo).
        scm.MapPost("/connections/{id:guid}/test", async (
            Guid id, ICurrentUserAccessor current,
            IScmConnectionService svc, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            var diag = await svc.TestAsync(id, ct);
            return Results.Ok(diag);
        }).RequireAuthorization("perm:manage_integrations");

        conns.MapDelete("/{id:guid}", async (
            Guid org_id, Guid id, ICurrentUserAccessor current,
            IScmConnectionService svc, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            return await svc.DeleteAsync(id, ct)
                ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization("perm:manage_integrations");

        // Webhook público (HMAC + idempotência; sem auth de usuário).
        app.MapPost("/api/v1/webhooks/scm/{kind}", async (
            string kind, HttpRequest req, IScmWebhookService svc,
            CancellationToken ct) =>
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
                    token = req.Headers["X-OpenHandsNet-Secret"]
                        .FirstOrDefault()
                        ?? req.Query["secret"].FirstOrDefault();
                    try
                    {
                        using var peek = JsonDocument.Parse(payload);
                        eventType = peek.RootElement
                            .TryGetProperty("eventType", out var et)
                                ? et.GetString() : null;
                        delivery = peek.RootElement
                            .TryGetProperty("notificationId", out var nd)
                            && nd.ValueKind == JsonValueKind.Number
                                ? nd.GetInt32().ToString()
                                : peek.RootElement.TryGetProperty(
                                        "id", out var pid)
                                    ? pid.ToString() : null;
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
        });

        return app;
    }
}

/// <summary>Manifest-flow request.</summary>
public sealed record ScmManifestRequest(
    string RedirectUri, string? AppName);

/// <summary>Pull-request request body.</summary>
public sealed record ScmPrRequest(
    string Title,
    string Body,
    string? Base = null,
    string? Head = null,
    bool? Draft = null,
    IReadOnlyList<string>? Reviewers = null);

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
