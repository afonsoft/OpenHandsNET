using Microsoft.AspNetCore.Mvc;

using Sandcastle.Api.DTOs;
using Sandcastle.Api.Policies;
using Sandcastle.Application.Conversations;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// SPEC-150 — handoff local↔cloud:
/// <c>POST /api/v1/conversations/handoff</c> cria a conversa com o patch
/// do working tree (start-task assíncrono — mesmas políticas de criação)
/// e <c>GET /api/v1/conversations/{id}/handoff-package</c> gera/sob demanda
/// o package do diff do sandbox (snapshot+TTL; owner/org).
/// </summary>
public static class HandoffEndpoints
{
    /// <summary>Mapeia os endpoints de handoff.</summary>
    public static void MapHandoffEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/conversations")
            .WithTags("Conversations");

        group.MapPost("/handoff",
            async (HandoffRequestDto req, IHandoffService svc,
                CancellationToken ct) =>
            {
                var result = await svc.CreateAsync(
                    new HandoffRequest
                    {
                        Repository = req.Repository ?? "",
                        BaseSha = req.BaseSha ?? "",
                        BaseBranch = req.BaseBranch,
                        DiffPatch = req.DiffPatch ?? "",
                        Message = req.Message,
                        Metadata = req.Metadata,
                    }, ct);
                if (result.Task is { } task)
                {
                    return Results.Accepted(
                        $"/api/v1/start-tasks/{task.Id}",
                        AppConversationEndpoints.ToDto(task));
                }

                return Results.Problem(detail: result.Detail,
                    statusCode: StatusCodes.Status400BadRequest);
            })
            .RequireScope("conversations:write")
            .WithName("CreateHandoff")
            .WithSummary("Hand off local working-tree changes to a new "
                + "cloud conversation (SPEC-150)")
            .WithDescription("Creates a conversation from a unified diff "
                + "of local uncommitted changes. Returns an async start "
                + "task — normal creation quotas/permissions/budgets "
                + "apply. 400: invalid repository/base_sha/patch; "
                + "403: forbidden (scope or plan policy); 409: conflict; "
                + "422: patch exceeds Handoff:MaxDiffKb.")
            .Produces<StartTaskDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/{id:guid}/handoff-package",
            async (Guid id, IHandoffService svc, CancellationToken ct,
                [FromQuery(Name = "regenerate")] bool regenerate = false) =>
            {
                var result = await svc.GetPackageAsync(id, regenerate, ct);
                return result.Error switch
                {
                    null => Results.Ok(result.Package),
                    HandoffPackageError.NotFound =>
                        Results.Problem(detail: "conversation not found",
                            statusCode: StatusCodes.Status404NotFound),
                    HandoffPackageError.Forbidden =>
                        Results.Problem(
                            detail: "handoff package forbidden",
                            statusCode: StatusCodes.Status403Forbidden),
                    HandoffPackageError.PackageTooLarge =>
                        Results.Problem(
                            detail: "sandbox diff exceeds "
                                + "Handoff:MaxDiffKb",
                            statusCode: StatusCodes
                                .Status422UnprocessableEntity),
                    _ => Results.Problem(
                        detail: "live sandbox unavailable and no cached "
                            + "snapshot — commits were not exported",
                        statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireScope("conversations:read")
            .WithName("GetHandoffPackage")
            .WithSummary("Handoff package — sandbox diff vs base "
                + "(SPEC-150)")
            .WithDescription("On-demand package of the live sandbox "
                + "diff (uncommitted + committed changes vs base), "
                + "cached for Handoff:PackageTtlMin minutes; "
                + "?regenerate=1 bypasses. Dead sandboxes serve the "
                + "last snapshot until it is invalidated by new HEAD.")
            .Produces<HandoffPackage>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }
}