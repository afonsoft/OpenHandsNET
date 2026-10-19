using Microsoft.AspNetCore.Mvc;
using OpenHandsNET.Application.Delegation;

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
            .ExcludeFromDescription();

        // RF-001: Delegar nova subtarefa a partir de uma conversa coordenadora
        group.MapPost("/app-conversations/{id:guid}/delegations", async (
            Guid id,
            [FromBody] DelegateTaskRequest req,
            [FromServices] IDelegationService delegationService,
            CancellationToken ct) =>
        {
            try
            {
                var res = await delegationService.DelegateAsync(id, req, ct);
                return Results.Created($"/api/v1/app-conversations/{res.ChildConversationId}", res);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
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
