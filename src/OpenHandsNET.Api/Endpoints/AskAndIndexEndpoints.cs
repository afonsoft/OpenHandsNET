using Microsoft.AspNetCore.Mvc;
using OpenHandsNET.Application.Ask;
using OpenHandsNET.Application.Indexing;

namespace OpenHandsNET.Api.Endpoints;

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
}
