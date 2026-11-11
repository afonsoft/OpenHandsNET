using Microsoft.AspNetCore.Mvc;

using Sandcastle.Application.Identity;
using Sandcastle.Application.Wiki;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// SPEC-145 RF-002 — endpoints da wiki por repo: geração (job/202),
/// árvore de tópicos e páginas. Permissões:
/// <c>perm:indexing.manage</c> para gerar; <c>perm:wiki.read</c> para
/// ler. Escopo = org efetiva do caller (mesma resolução do
/// <c>/indexing/status</c>).
/// </summary>
public static class WikiEndpoints
{
    /// <summary>Policy de quem pode indexar/gerar (SPEC-145 security).</summary>
    internal const string PermIndexingManage = "perm:indexing.manage";

    /// <summary>Policy de leitura da wiki (SPEC-145 security).</summary>
    internal const string PermWikiRead = "perm:wiki.read";

    /// <summary>Mapeia <c>/api/v1/repos/{repo}/wiki*</c>.</summary>
    public static IEndpointRouteBuilder MapWikiEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/repos")
            .WithTags("Wiki")
            .ExcludeFromDescription();

        // POST /repos/{repo}/wiki/generate — cria o run (202) quando o
        // índice do repo está pronto; 409 quando não está.
        group.MapPost("/{repo}/wiki/generate", async (
            string repo,
            [FromBody] GenerateWikiRequest req,
            [FromServices] ICurrentUserAccessor current,
            [FromServices] IEffectiveOrgResolver resolver,
            [FromServices] IWikiService wiki,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }
            var orgId = (await resolver.ResolveAsync(user, null, ct))?.OrgId;
            var run = await wiki.GenerateAsync(
                Uri.UnescapeDataString(repo), req.Branch, req.Effort,
                orgId, user.UserId.ToString(), ct);
            return run is null
                ? Results.Conflict(new
                {
                    error = "repo_index_not_ready",
                    repository = repo,
                })
                : Results.Accepted(
                    $"/api/v1/repos/{repo}/wiki/runs/{run.Id}", run);
        }).WithTags(EndpointTags.Api)
            .RequireAuthorization(PermIndexingManage);

        // GET /repos/{repo}/wiki/runs/{runId} — status do job de geração.
        group.MapGet("/{repo}/wiki/runs/{runId:guid}", async (
            string repo,
            Guid runId,
            [FromServices] IWikiService wiki,
            CancellationToken ct) =>
        {
            var run = await wiki.GetRunAsync(runId, ct);
            return run is null
                ? Results.NotFound()
                : Results.Ok(run);
        }).WithTags(EndpointTags.Api)
            .RequireAuthorization(PermWikiRead);

        // GET /repos/{repo}/wiki?branch= — árvore de tópicos do último run.
        group.MapGet("/{repo}/wiki", async (
            string repo,
            [FromQuery] string? branch,
            [FromServices] ICurrentUserAccessor current,
            [FromServices] IEffectiveOrgResolver resolver,
            [FromServices] IWikiService wiki,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }
            var orgId = (await resolver.ResolveAsync(user, null, ct))?.OrgId;
            var tree = await wiki.GetTreeAsync(
                Uri.UnescapeDataString(repo), branch, orgId, ct);
            return Results.Ok(tree);
        }).WithTags(EndpointTags.Api)
            .RequireAuthorization(PermWikiRead);

        // GET /repos/{repo}/wiki/pages/{pageId} — página completa (markdown).
        group.MapGet("/{repo}/wiki/pages/{pageId:guid}", async (
            string repo,
            Guid pageId,
            [FromServices] IWikiService wiki,
            CancellationToken ct) =>
        {
            var page = await wiki.GetPageAsync(pageId, ct);
            return page is null
                ? Results.NotFound()
                : Results.Ok(page);
        }).WithTags(EndpointTags.Api)
            .RequireAuthorization(PermWikiRead);

        return app;
    }

    /// <summary>Payload do POST de geração.</summary>
    public sealed record GenerateWikiRequest(
        string? Branch = null,
        string? Effort = null);
}