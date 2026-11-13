using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Alerts;
using Sandcastle.Application.Contracts;
using Sandcastle.Application.Identity;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// <c>/api/v1/alerts</c> — SPEC-151: fontes de alertas (Sentry/
/// PagerDuty/generic) CRUD, eventos list/patch/create-session e o
/// webhook inbound <c>/api/v1/webhooks/alerts/{source_id}</c>
/// (signature-only auth — fail-closed 401).
/// </summary>
public static class AlertEndpoints
{
    private const int MaxWebhookPayload = 256 * 1024;

    /// <summary>Maps alert endpoints.</summary>
    public static IEndpointRouteBuilder MapAlertEndpoints(
        this IEndpointRouteBuilder app)
    {
        var alerts = app.MapGroup("/api/v1/alerts").WithTags("Alerts");

        alerts.MapPost("/sources", CreateSourceAsync)
            .RequireAuthorization("perm:alerts.manage");
        alerts.MapGet("/sources", ListSourcesAsync)
            .RequireAuthorization("perm:alerts.view");
        alerts.MapGet("/sources/{id:guid}", GetSourceAsync)
            .RequireAuthorization("perm:alerts.view");
        alerts.MapPatch("/sources/{id:guid}", PatchSourceAsync)
            .RequireAuthorization("perm:alerts.manage");
        alerts.MapDelete("/sources/{id:guid}", DeleteSourceAsync)
            .RequireAuthorization("perm:alerts.manage");

        alerts.MapGet("/events", ListEventsAsync)
            .RequireAuthorization("perm:alerts.view");
        alerts.MapGet("/events/{id:guid}", GetEventAsync)
            .RequireAuthorization("perm:alerts.view");
        alerts.MapPatch("/events/{id:guid}", PatchEventAsync)
            .RequireAuthorization("perm:alerts.manage");
        alerts.MapPost("/events/{id:guid}/create-session",
                CreateSessionAsync)
            .RequireAuthorization("perm:alerts.manage");

        // Inbound público — autenticação é a signature do provider.
        app.MapPost("/api/v1/webhooks/alerts/{source_id:guid}",
            HandleAlertWebhookAsync).WithTags(EndpointTags.Api);

        return app;
    }

    // ---- sources ----------------------------------------------------

    private static async Task<IResult> CreateSourceAsync( // NOSONAR S107 — minimal-API handler: bindings individuais
        [FromBody] AlertSourceRequest req, HttpContext http,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        IAlertService svc, ISecretStore secrets,
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
            return SandcastleProblemFactory.Create(http, 404, "no org");
        }
        var provider = AlertProviderExtensions.Parse(req.Provider);
        if (provider is null)
        {
            return SandcastleProblemFactory.Create(http, 400,
                "provider must be sentry|pagerduty|generic");
        }
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "name is required");
        }
        if (!AlertSeverities.All.Contains(req.TriageSeverityMin))
        {
            return SandcastleProblemFactory.Create(http, 400,
                "triage_severity_min must be "
                + "critical|high|medium|low|info");
        }

        var secretRef = await ResolveSecretRefAsync(
            secrets, org.OrgId, provider.Value, req.SecretRef,
            req.SecretValue, null, ct);
        if (secretRef is null)
        {
            return SandcastleProblemFactory.Create(http, 400,
                "secret_ref or secret_value is required");
        }

        var source = await svc.CreateSourceAsync(
            org.OrgId, provider.Value, req.Name, secretRef,
            req.RepoMapping, req.Enabled, req.AutoCreateSession,
            req.TriageSeverityMin, user.UserId.ToString(), ct);
        return Results.Ok(ToResponse(source));
    }

    private static async Task<IResult> ListSourcesAsync(
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IAlertService svc,
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
            return Results.Ok(new { items = Array.Empty<object>() });
        }
        var sources = await svc.ListSourcesAsync(org.OrgId, ct);
        return Results.Ok(new
        {
            items = sources.Select(ToResponse).ToList(),
        });
    }

    private static async Task<IResult> GetSourceAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IAlertService svc,
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
        var source = await svc.GetSourceAsync(id, org.OrgId, ct);
        return source is null
            ? Results.NotFound() : Results.Ok(ToResponse(source));
    }

    private static async Task<IResult> PatchSourceAsync( // NOSONAR S107 — minimal-API handler: bindings individuais
        Guid id, [FromBody] AlertSourcePatchRequest req,
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IAlertService svc,
        ISecretStore secrets, CancellationToken ct)
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
        if (req.TriageSeverityMin is { } sev
            && !AlertSeverities.All.Contains(sev))
        {
            return SandcastleProblemFactory.Create(http, 400,
                "triage_severity_min must be "
                + "critical|high|medium|low|info");
        }

        var existing = await svc.GetSourceAsync(id, org.OrgId, ct);
        if (existing is null)
        {
            return Results.NotFound();
        }
        var secretRef = await ResolveSecretRefAsync(
            secrets, org.OrgId, existing.Provider, req.SecretRef,
            req.SecretValue, existing.SecretRef, ct);

        var updated = await svc.UpdateSourceAsync(
            id, org.OrgId, req.Name, secretRef, req.RepoMapping,
            req.Enabled, req.AutoCreateSession,
            req.TriageSeverityMin, ct);
        return updated is null
            ? Results.NotFound() : Results.Ok(ToResponse(updated));
    }

    private static async Task<IResult> DeleteSourceAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IAlertService svc,
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
        return await svc.DeleteSourceAsync(id, org.OrgId, ct)
            ? Results.NoContent() : Results.NotFound();
    }

    // ---- events -----------------------------------------------------

    private static async Task<IResult> ListEventsAsync( // NOSONAR S107 — minimal-API handler: filtros query bound
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IAlertService svc,
        Guid? source_id, string? provider, string? severity,
        string? status, string? repository, bool? unrouted,
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
                next_page_id = (string?)null,
            });
        }
        var page = await svc.SearchEventsAsync(org.OrgId,
            new AlertEventQuery(
                source_id, provider, severity, status, repository,
                unrouted ?? false, page_id, limit ?? 50), ct);
        return Results.Ok(new
        {
            items = page.Items.Select(ToResponse).ToList(),
            next_page_id = page.NextPageId,
        });
    }

    private static async Task<IResult> GetEventAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IAlertService svc,
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
        var evt = await svc.GetEventAsync(id, org.OrgId, ct);
        return evt is null
            ? Results.NotFound() : Results.Ok(ToResponse(evt));
    }

    private static async Task<IResult> PatchEventAsync(
        Guid id, [FromBody] AlertEventPatchRequest req,
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IAlertService svc,
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
        if (req.Status is { } st
            && !AlertEventStatuses.Updatable.Contains(st))
        {
            return SandcastleProblemFactory.Create(http, 400,
                "status must be dismissed|resolved|new");
        }
        var evt = await svc.UpdateEventAsync(
            id, org.OrgId, req.Status, req.Repository, ct);
        return evt is null
            ? Results.NotFound() : Results.Ok(ToResponse(evt));
    }

    private static async Task<IResult> CreateSessionAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IAlertService svc,
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
        var result = await svc.CreateSessionAsync(
            id, org.OrgId, user.UserId.ToString(), ct);
        return result switch
        {
            { ConversationId: { } convId } => Results.Accepted(
                $"/api/v1/app-conversations/{convId}",
                new { conversation_id = convId }),
            { Error: AlertSessionError.Unrouted } =>
                SandcastleProblemFactory.Create(http, 409,
                    "alert event has no mapped repository — "
                    + "route it first via PATCH"),
            { Error: AlertSessionError.Duplicate } =>
                SandcastleProblemFactory.Create(http, 409,
                    "a session already exists for this alert"),
            { Error: AlertSessionError.QuotaExceeded } =>
                SandcastleProblemFactory.Create(http, 403,
                    "hourly alert session quota reached"),
            _ => SandcastleProblemFactory.Create(
                http, 404, "alert event not found"),
        };
    }

    // ---- inbound webhook ----------------------------------------------

    /// <summary>
    /// <c>POST /api/v1/webhooks/alerts/{source_id}</c> — signature
    /// per provider (Sentry <c>sentry-hook-signature</c> HMAC,
    /// PagerDuty <c>X-PagerDuty-Signature</c> v1 chain, generic
    /// shared-secret header). Fail-closed: sem signature válida →
    /// 401, payload nunca processado.
    /// </summary>
    private static async Task<IResult> HandleAlertWebhookAsync(
        Guid source_id, HttpRequest req, IAlertIngestService svc,
        CancellationToken ct)
    {
        if ((req.ContentLength ?? 0) > MaxWebhookPayload)
        {
            return Results.StatusCode(413);
        }
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms, ct);
        var payload = ms.ToArray();

        var headers = new Dictionary<string, string?>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var h in req.Headers)
        {
            headers[h.Key] = h.Value.FirstOrDefault();
        }

        var result = await svc.HandleAsync(source_id, headers, payload, ct);
        return result switch
        {
            AlertIngestResult.InvalidSignature => Results.Unauthorized(),
            AlertIngestResult.NoSource =>
                Results.NotFound(new { error = "no_source" }),
            AlertIngestResult.RateLimited =>
                Results.StatusCode(429),
            AlertIngestResult.Duplicate =>
                Results.Ok(new { status = "duplicate" }),
            AlertIngestResult.Ignored =>
                Results.Ok(new { status = "ignored" }),
            _ => Results.Ok(new { status = "processed" }),
        };
    }

    // ---- helpers --------------------------------------------------------

    /// <summary>
    /// Resolve o <c>secret_ref</c> efetivo: <paramref name="secretValue"/>
    /// (novo segredo em claro) é guardado no ISecretStore sob nome
    /// auto-gerado; <paramref name="secretRef"/> aponta um segredo já
    /// existente; <paramref name="fallback"/> mantém o atual.
    /// </summary>
    private static async Task<string?> ResolveSecretRefAsync(
        ISecretStore secrets, Guid orgId, AlertProvider provider,
        string? secretRef, string? secretValue, string? fallback,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(secretValue))
        {
            var name = $"alert-{provider.ToWire()}-{Guid.NewGuid():N}"[..28];
            await secrets.SetAsync("org", orgId.ToString(), name,
                secretValue, ct);
            return name;
        }
        if (!string.IsNullOrWhiteSpace(secretRef))
        {
            return secretRef.Trim();
        }
        return fallback;
    }

    /// <summary>Serialização da fonte — <c>webhook_url</c> de conveniência.</summary>
    private static object ToResponse(AlertSource s) => new
    {
        id = s.Id,
        org_id = s.OrgId,
        provider = s.Provider.ToWire(),
        name = s.Name,
        secret_ref = s.SecretRef,
        repo_mapping = s.RepoMapping,
        enabled = s.Enabled,
        auto_create_session = s.AutoCreateSession,
        triage_severity_min = s.TriageSeverityMin,
        webhook_url = $"/api/v1/webhooks/alerts/{s.Id}",
        created_at = s.CreatedAt,
        updated_at = s.UpdatedAt,
    };

    /// <summary>Serialização do evento (session/pr links).</summary>
    private static object ToResponse(AlertEvent e) => new
    {
        id = e.Id,
        org_id = e.OrgId,
        source_id = e.SourceId,
        provider = e.Provider.ToWire(),
        external_id = e.ExternalId,
        kind = e.Kind,
        title = e.Title,
        url = e.Url,
        severity = e.Severity,
        service = e.Service,
        culprit = e.Culprit,
        repository = e.Repository,
        unrouted = e.Unrouted,
        labels = e.LabelsJson is null ? null
            : (object?)JsonSerializer.Deserialize<JsonElement>(
                e.LabelsJson),
        first_seen = e.FirstSeen,
        status = e.Status,
        session_id = e.SessionId,
        pr_url = e.PrUrl,
        created_at = e.CreatedAt,
        updated_at = e.UpdatedAt,
    };
}

/// <summary>Create alert source request.</summary>
public sealed record AlertSourceRequest(
    string Provider,
    string Name,
    string? SecretRef = null,
    string? SecretValue = null,
    IReadOnlyDictionary<string, string>? RepoMapping = null,
    bool Enabled = true,
    bool AutoCreateSession = false,
    string TriageSeverityMin = "high");

/// <summary>PATCH alert source — campos nulos preservam o atual.</summary>
public sealed record AlertSourcePatchRequest(
    string? Name = null,
    string? SecretRef = null,
    string? SecretValue = null,
    IReadOnlyDictionary<string, string>? RepoMapping = null,
    bool? Enabled = null,
    bool? AutoCreateSession = null,
    string? TriageSeverityMin = null);

/// <summary>PATCH alert event — <c>{status, repository}</c>.</summary>
public sealed record AlertEventPatchRequest(
    string? Status = null,
    string? Repository = null);