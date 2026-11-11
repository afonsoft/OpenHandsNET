using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Ask;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Indexing;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// SPEC-60: Endpoints para Repo Indexing e Ask Mode Q&amp;A.
/// </summary>
public static class AskAndIndexEndpoints
{
    public static IEndpointRouteBuilder MapAskAndIndexEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1")
            .WithTags("AskAndIndex")
            .ExcludeFromDescription();

        // RF-001: Trigger index e consulta de status
        group.MapPost("/repos/index", async (
            [FromBody] TriggerIndexApiRequest req,
            [FromServices] IRepoIndexer indexer,
            CancellationToken ct) =>
        {
            var res = await indexer.TriggerIndexAsync(req.Repository, req.Branch ?? "main", req.OrgId, ct);
            return Results.Accepted($"/api/v1/repos/index/{res.Id}", res);
        });

        group.MapGet("/repos/index/{id:guid}", async (
            Guid id,
            [FromServices] IRepoIndexer indexer,
            CancellationToken ct) =>
        {
            var status = await indexer.GetStatusAsync(id, ct);
            return status is null ? Results.NotFound() : Results.Ok(status);
        });

        // SPEC-147 RF-005: agregado "X/Y repos indexed" — escopo caller
        // (org efetiva quando há; senão repos das próprias conversas).
        group.MapGet("/indexing/status", async (
            [FromServices] ICurrentUserAccessor current,
            [FromServices] IEffectiveOrgResolver resolver,
            [FromServices] AppDbContext db,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var orgId = (await resolver.ResolveAsync(user, null, ct))?.OrgId;
            var uid = user.UserId.ToString();

            // Connected = repos selecionados em conversas do escopo.
            var connected = orgId is { } oid
                ? await db.Conversations.AsNoTracking()
                    .Where(c => c.OrgId == oid && c.SelectedRepository != null)
                    .Select(c => c.SelectedRepository!)
                    .Distinct().ToListAsync(ct)
                : await db.Conversations.AsNoTracking()
                    .Where(c => c.CreatedByUserId == uid
                        && c.SelectedRepository != null)
                    .Select(c => c.SelectedRepository!)
                    .Distinct().ToListAsync(ct);

            // Index records do escopo; sem org, só os records globais
            // cujo repo está entre os conectados do caller. Ordena em
            // memória — SQLite não ordena DateTimeOffset no SQL.
            var rows = orgId is { } oid2
                ? await db.RepoIndexes.AsNoTracking()
                    .Where(r => r.OrgId == oid2)
                    .Select(r => new { r.Repository, r.Status, r.UpdatedAt })
                    .ToListAsync(ct)
                : await db.RepoIndexes.AsNoTracking()
                    .Where(r => r.OrgId == null
                        && connected.Contains(r.Repository))
                    .Select(r => new { r.Repository, r.Status, r.UpdatedAt })
                    .ToListAsync(ct);
            var latest = rows.OrderByDescending(r => r.UpdatedAt);

            var connectedSet = new HashSet<string>(
                connected, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var indexed = 0;
            var indexing = 0;
            var failed = 0;
            foreach (var r in latest)
            {
                if (!seen.Add(r.Repository))
                {
                    continue; // só o record mais recente por repo conta
                }

                connectedSet.Add(r.Repository);
                switch (r.Status)
                {
                    case "ready":
                    case "stale":
                        indexed++;
                        break;
                    case "indexing":
                        indexing++;
                        break;
                    default:
                        failed++;
                        break;
                }
            }

            return Results.Ok(new IndexingStatusDto(
                connectedSet.Count, indexed, indexing, failed));
        }).WithTags(EndpointTags.Api).RequireAuthorization();

        // SPEC-145 RF-001: re-index manual por (repo, branch) — mesma
        // trigger de /repos/index mas com permissão explícita de gestão
        // (perm:indexing.manage). Escopo = org efetiva do caller.
        group.MapPost("/indexing/reindex", async (
            [FromBody] TriggerIndexApiRequest req,
            [FromServices] ICurrentUserAccessor current,
            [FromServices] IEffectiveOrgResolver resolver,
            [FromServices] IRepoIndexer indexer,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }
            var orgId = (await resolver.ResolveAsync(user, null, ct))?.OrgId;
            var res = await indexer.TriggerIndexAsync(
                req.Repository, req.Branch ?? "main", orgId, ct);
            return Results.Accepted($"/api/v1/repos/index/{res.Id}", res);
        }).WithTags(EndpointTags.Api)
            .RequireAuthorization(WikiEndpoints.PermIndexingManage);

        // RF-002: Pergunta em modo Ask
        group.MapPost("/ask-sessions", async (
            [FromBody] AskRequest req,
            [FromServices] IAskModeService askService,
            CancellationToken ct) =>
        {
            var res = await askService.AskAsync(req, ct);
            return Results.Ok(res);
        });

        // RF-002: Promover para sessão ativa de agente
        group.MapPost("/ask-sessions/promote", async (
            [FromBody] PromoteAskSessionRequest req,
            [FromServices] IAskModeService askService,
            CancellationToken ct) =>
        {
            var res = await askService.PromoteToAgentAsync(req, ct);
            return Results.Ok(res);
        });

        return app;
    }

    public sealed record TriggerIndexApiRequest(
        string Repository,
        string? Branch = "main",
        Guid? OrgId = null);

    /// <summary>SPEC-147 RF-005: resumo X/Y do badge de indexação.</summary>
    public sealed record IndexingStatusDto(
        int TotalConnected,
        int Indexed,
        int Indexing,
        int Failed);
}