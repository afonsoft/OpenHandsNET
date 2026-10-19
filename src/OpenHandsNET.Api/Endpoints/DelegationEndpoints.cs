using Microsoft.AspNetCore.Mvc;
using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Delegation;
using OpenHandsNET.Application.Identity;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// SPEC-62: Endpoints para gerenciamento de conversas delegadas (Managed Conversations).
/// </summary>
public static class DelegationEndpoints
{
    public static IEndpointRouteBuilder MapDelegationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1")
            .WithTags("Delegations")
            .RequireAuthorization()
            .ExcludeFromDescription();

        // RF-001: Delegar nova subtarefa a partir de uma conversa coordenadora
        group.MapPost("/app-conversations/{id:guid}/delegations", async (
            Guid id,
            [FromBody] DelegateTaskRequest req,
            [FromServices] IDelegationService delegationService,
            [FromServices] ICurrentUserAccessor current,
            [FromServices] IEffectiveOrgResolver orgs,
            [FromServices] IAuditService audit,
            [FromServices] Application.Security.IInputGuard inputGuard,
            HttpContext http,
            CancellationToken ct) =>
        {
            // SPEC-59 RF-002: mesmo screening do create/send-message.
            var org = current.User is { } u
                ? await orgs.ResolveAsync(u, null, ct) : null;
            var verdict = await inputGuard.ScoreAsync(
                req.TaskDescription, "ui", org?.OrgId, ct);
            if (verdict.Verdict
                == Application.Security.InputGuardVerdict.Block)
            {
                await audit.RecordAsync(
                    "guard.input_blocked",
                    current.User?.UserId.ToString(), org?.OrgId,
                    "app_conversation", id.ToString(),
                    metadata: new { categories = verdict.Categories });
                return (IResult)OpenHandsNetProblemFactory.Create(
                    http, 403, "input_blocked");
            }

            try
            {
                var res = await delegationService.DelegateAsync(id, req, ct);
                return (IResult)Results.Created(
                    $"/api/v1/app-conversations/{res.ChildConversationId}", res);
            }
            catch (KeyNotFoundException)
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 404, $"Conversation {id} not found");
            }
            catch (InvalidOperationException ex)
            {
                return (IResult)Results.BadRequest(new { error = ex.Message });
            }
        });

        // RF-002: Listar conversas-irmãs (siblings) e coordenador
        group.MapGet("/app-conversations/{id:guid}/delegations", async (
            Guid id,
            [FromServices] IDelegationService delegationService,
            CancellationToken ct) =>
        {
            var list = await delegationService.ListSiblingsAsync(id, ct);
            return Results.Ok(list);
        });

        return app;
    }
}
