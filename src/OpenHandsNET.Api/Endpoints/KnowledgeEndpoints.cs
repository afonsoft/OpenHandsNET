using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Application.Knowledge;
using OpenHandsNET.Infrastructure.Knowledge;
using OpenHandsNET.Application.Memory;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>SPEC-63: knowledge pipeline — suggestions + groom.</summary>
public static class KnowledgeEndpoints
{
    public static WebApplication MapKnowledgeEndpoints(
        this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/knowledge").RequireAuthorization();

        g.MapGet("/suggestions", async (string? status, string? scope,
            IKnowledgeService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListSuggestionsAsync(
                status is null ? null
                    : Enum.Parse<SuggestionStatus>(status, true),
                scope, ct)));
        g.MapPost("/suggestions", async (SuggestRequest body,
            IKnowledgeService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Content))
                return Results.BadRequest(
                    new { error = "content_required" });
            var s = await svc.SuggestAsync(
                body.Title ?? "suggestion", body.Content,
                body.Scope ?? "org", body.RepoPath,
                SuggestionKind.Entry, "manual", null, null, ct);
            return Results.Created(
                $"/api/v1/knowledge/suggestions/{s.Id}", s);
        });
        g.MapPost("/suggestions/{id:guid}/approve", async (Guid id,
            ApproveRequest? body, IKnowledgeService svc,
            ICurrentUserAccessor current, HttpContext http,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await svc.ApproveAsync(id,
                    body?.ContentOverride,
                    current.User?.UserId.ToString(), ct));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (SuggestionResolvedException)
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 409, "suggestion_resolved");
            }
        });
        g.MapPost("/suggestions/{id:guid}/dismiss", async (Guid id,
            DismissRequest? body, IKnowledgeService svc,
            HttpContext http, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await svc.DismissAsync(
                    id, body?.Reason, ct));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (SuggestionResolvedException)
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 409, "suggestion_resolved");
            }
        });
        g.MapPost("/groom", async (IKnowledgeService svc,
            CancellationToken ct) =>
            Results.Ok(await svc.GroomAsync(ct)))
            .RequireAuthorization("perm:edit_org_settings");
        g.MapGet("/entries", async (string? query,
            IMemoryService memory, IEffectiveOrgResolver orgs,
            ICurrentUserAccessor current, HttpContext http,
            CancellationToken ct) =>
            current.User is { } u
                && await orgs.ResolveAsync(
                    u, http.Request.Headers["X-Org-Id"], ct) is { } org
                ? Results.Ok(await memory.ListOrgAsync(
                    org.OrgId, query, ct))
                : Results.Ok(Array.Empty<object>()));
        return app;
    }

    public sealed record SuggestRequest(
        string? Title, string Content, string? Scope,
        string? RepoPath);
    public sealed record ApproveRequest(string? ContentOverride);
    public sealed record DismissRequest(string? Reason);
}
