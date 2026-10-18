using System.Text.Json;
using OpenHandsNET.Application.Tickets;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// SPEC-55: webhooks públicos de tickets
/// <c>/api/v1/webhooks/tickets/{linear,jira}</c> + admin
/// <c>/orgs/{org}/ticket-connections</c>, diagnóstico <c>test</c>,
/// run manual e comentário manual.
/// </summary>
public static class TicketEndpoints
{
    /// <summary>Mapeia os endpoints.</summary>
    public static void MapTicketEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/webhooks/tickets/linear", async (
            HttpRequest req, ITicketTriggerService svc,
            CancellationToken ct) =>
        {
            var payload = await ReadBody(req);
            var sig = req.Headers["Linear-Signature"]
                .FirstOrDefault();
            var hint = ExtractLinearWorkspace(payload);
            var r = await svc.HandleAsync(
                TicketKind.Linear, sig, hint, payload, ct);
            return MapResult(r);
        }).AllowAnonymous();

        app.MapPost("/api/v1/webhooks/tickets/jira", async (
            HttpRequest req, ITicketTriggerService svc,
            CancellationToken ct) =>
        {
            var payload = await ReadBody(req);
            var secret = req.Headers["X-OpenHandsNet-Secret"]
                .FirstOrDefault()
                ?? req.Query["secret"].FirstOrDefault();
            var hint = ExtractJiraHost(payload);
            var r = await svc.HandleAsync(
                TicketKind.Jira, secret, hint, payload, ct);
            return MapResult(r);
        }).AllowAnonymous();

        var conns = app.MapGroup(
            "/api/v1/orgs/{org_id:guid}/ticket-connections")
            .RequireAuthorization("perm:manage_integrations");

        conns.MapGet("", async (
            Guid org_id, ITicketConnectionService svc,
            CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(org_id, ct)));

        conns.MapPost("", async (
            Guid org_id, TicketConnectionRequest body,
            ITicketConnectionService svc,
            OpenHandsNET.Application.Identity.ICurrentUserAccessor current,
            CancellationToken ct) =>
        {
            var kind = TicketKindExtensions.Parse(body.Kind);
            if (kind is null)
            {
                return Results.BadRequest(
                    new { detail = "kind must be linear|jira" });
            }
            var c = await svc.CreateAsync(
                org_id, kind.Value, body.ApiUrl, body.TokenSecretRef,
                body.WebhookSecretRef, body.WorkspaceId,
                body.TriggerConfigJson,
                current.User?.UserId.ToString() ?? "system", ct);
            return Results.Created(
                $"/api/v1/orgs/{org_id}/ticket-connections/{c.Id}", c);
        });

        conns.MapDelete("/{id:guid}", async (
            Guid id, ITicketConnectionService svc,
            CancellationToken ct) =>
            await svc.DeleteAsync(id, ct)
                ? Results.NoContent() : Results.NotFound());

        conns.MapGet("/{id:guid}/projects", async (
            Guid id, ITicketConnectionService svc,
            CancellationToken ct) =>
            Results.Ok(await svc.ListProjectConfigsAsync(id, ct)));

        conns.MapPost("/{id:guid}/projects", async (
            Guid id, TicketProjectConfigRequest body,
            ITicketConnectionService svc,
            CancellationToken ct) =>
            Results.Ok(await svc.SetProjectConfigAsync(
                id, body.ProjectKey, body.SelectedRepository,
                body.SelectedBranch, ct)));

        app.MapPost("/api/v1/tickets/connections/{id:guid}/test",
            async (
                Guid id, ITicketConnectionService svc,
                IReadOnlyDictionary<TicketKind, ITicketProvider> providers,
                CancellationToken ct) =>
            {
                var c = await svc.GetAsync(id, ct);
                if (c is null
                    || !providers.TryGetValue(c.Kind, out var p))
                {
                    return Results.NotFound();
                }
                var (ok, detail) = await p.TestConnectionAsync(c, ct);
                return Results.Ok(new { ok, detail });
            }).RequireAuthorization("perm:manage_integrations");

        app.MapPost("/api/v1/tickets/connections/{id:guid}/run",
            async (
                Guid id, TicketRunRequest body,
                ITicketTriggerService svc, CancellationToken ct) =>
            {
                var r = await svc.RunAsync(id, body.IssueKey, ct);
                return MapResult(r);
            }).RequireAuthorization("perm:manage_integrations");

        app.MapPost("/api/v1/tickets/{kind}/{issue_key}/comment",
            async (
                string kind, string issue_key, TicketCommentRequest body,
                ITicketConnectionService svc,
                IReadOnlyDictionary<TicketKind, ITicketProvider> providers,
                CancellationToken ct) =>
            {
                var k = TicketKindExtensions.Parse(kind);
                if (k is null
                    || !providers.TryGetValue(k.Value, out var p))
                {
                    return Results.NotFound(
                        new { detail = "unknown ticket kind" });
                }
                var c = await svc.FindForWebhookAsync(
                    k.Value, body.WorkspaceId, ct);
                if (c is null)
                {
                    return Results.NotFound(
                        new { detail = "no connection" });
                }
                await p.AddCommentAsync(c, issue_key, body.Body, ct);
                return Results.Ok(new { ok = true });
            }).RequireAuthorization("perm:manage_integrations");
    }

    private static async Task<byte[]> ReadBody(HttpRequest req)
    {
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static IResult MapResult(TicketOpsResult r) => r switch
    {
        TicketOpsResult.Processed => Results.Ok(
            new { result = "processed" }),
        TicketOpsResult.Duplicate => Results.Ok(
            new { result = "duplicate" }),
        TicketOpsResult.Ignored => Results.Ok(
            new { result = "ignored" }),
        TicketOpsResult.InvalidSignature => Results.Unauthorized(),
        TicketOpsResult.NoConnection => Results.NotFound(
            new { detail = "no ticket connection" }),
        _ => Results.UnprocessableEntity(
            new { detail = "bad payload" }),
    };

    private static string? ExtractLinearWorkspace(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty(
                "organizationId", out var o) ? o.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? ExtractJiraHost(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("issue", out var i)
                && i.TryGetProperty("self", out var s))
            {
                return new Uri(s.GetString()!)
                    .GetLeftPart(UriPartial.Authority);
            }
            return null;
        }
        catch (Exception) { return null; }
    }
}

/// <summary>POST ticket-connections.</summary>
public sealed record TicketConnectionRequest(
    string Kind,
    string ApiUrl,
    string TokenSecretRef,
    string? WebhookSecretRef,
    string? WorkspaceId,
    string? TriggerConfigJson);

/// <summary>POST project config.</summary>
public sealed record TicketProjectConfigRequest(
    string ProjectKey,
    string? SelectedRepository,
    string? SelectedBranch);

/// <summary>POST run manual.</summary>
public sealed record TicketRunRequest(string IssueKey);

/// <summary>POST comentário manual.</summary>
public sealed record TicketCommentRequest(
    string Body, string? WorkspaceId);
