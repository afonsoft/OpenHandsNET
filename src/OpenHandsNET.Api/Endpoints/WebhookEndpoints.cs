using System.Text.Json;

using OpenHandsNET.Api.DTOs;
using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// <c>/api/v1/webhooks</c> — inbound webhooks: injeção de evento externo no
/// stream, criação de conversa por webhook, metadados e OAuth-state MCP.
/// </summary>
public static class WebhookEndpoints
{
    /// <summary>Maps webhook endpoints.</summary>
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/webhooks").WithTags("Webhooks");

        // SPEC-97 RF-001: ingress POSTs exigem auth (Bearer sk-*) — antes
        // eram escritas anônimas sem verificação (achado `ignored_auth`).

        // Injeta um evento externo (ex.: CI, GitHub webhook) no stream da conversa.
        group.MapPost("/events/{conversationId:guid}", async (
            Guid conversationId, WebhookEventRequest body,
            IEventService events, HttpContext http, CancellationToken ct) =>
        {
            var e = JsonSerializer.Deserialize<EventBase>(
                body.Event.GetRawText(), EventJson.Options);
            if (e is null)
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 400, "Invalid event payload");
            }

            await events.AppendAsync(conversationId, e, ct);
            return Results.Accepted();
        });

        // Cria conversa via webhook (mesmo caminho do POST /app-conversations).
        group.MapPost("/conversations", async (
            CreateAppConversationRequest body,
            IAppConversationService svc, CancellationToken ct) =>
        {
            var task = await svc.CreateAsync(new CreateConversationRequest(
                body.InitialMessage is null ? null : new InitialMessage(
                    body.InitialMessage.Role,
                    body.InitialMessage.Content.Cast<object>().ToList()),
                body.SelectedRepository, body.AgentType, body.LlmModel,
                body.SandboxSpecId), ct);
            return Results.Accepted(
                $"/api/v1/app-conversations/start-tasks/{task.Id}", new
                {
                    id = task.Id.ToString(),
                    status = task.Status.ToString().ToUpperInvariant(),
                    app_conversation_id = task.AppConversationId?.ToString(),
                });
        });

        // Upstream devolve metadados de secrets disponíveis p/ webhooks.
        group.MapGet("/secrets", () => Results.Ok(Array.Empty<object>()));

        // OAuth state exchange do MCP (SPEC-07 completa; aqui aceita e no-ops).
        // Anônimo: callback externo do servidor MCP (SPEC-97 RF-001).
        group.MapPost("/mcp-oauth-state", () => Results.Accepted())
            .AllowAnonymous();

        return app;
    }
}