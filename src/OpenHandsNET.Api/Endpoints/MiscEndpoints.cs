using System.Collections.Concurrent;

using OpenHandsNET.Api.DTOs;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// Misc endpoints: <c>/user</c>, <c>/skills</c>, <c>/pending-messages</c>
/// (fila em memória no MVP — durable queue vem com SPEC-10).
/// </summary>
public static class MiscEndpoints
{
    private static readonly ConcurrentDictionary<Guid, List<PendingMessageDto>> Pending = new();

    /// <summary>Maps misc endpoints.</summary>
    public static IEndpointRouteBuilder MapMiscEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/user", () => Results.Ok(new
        {
            user_id = "local",
            name = "OpenHandsNET Local User",
            deployment_mode = "oss",
        })).WithTags("User").AllowAnonymous();

        app.MapGet("/api/v1/pending-messages", (
            Guid conversation_id) =>
            Results.Ok(Pending.TryGetValue(conversation_id, out var list)
                ? list.ToArray() : [])).WithTags("Messages");

        app.MapPost("/api/v1/pending-messages", (PendingMessageDto body) =>
        {
            // SPEC-97 RF-002: input inválido → 400, não 500 (era Guid.Parse).
            if (!Guid.TryParse(body.ConversationId, out var conversationId))
            {
                return Results.BadRequest(
                    new { detail = "conversation_id must be a valid guid" });
            }

            var list = Pending.GetOrAdd(conversationId, _ => []);
            lock (list)
            {
                list.Add(body);
            }

            return Results.Created($"/api/v1/pending-messages/{body.Id}", body);
        }).WithTags("Messages");

        return app;
    }
}