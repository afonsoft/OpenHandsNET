using System.Text.Json;

using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// SPEC-40 RF-005: subset ACP (Agent Client Protocol) sobre REST+SSE —
/// <c>/api/acp/conversations</c> create+prompt, prompt, cancel e stream
/// <c>session/update</c>. Suficiente p/ editors abrirem e dirigirem uma
/// conversa; conversas ACP são conversas normais (aparecem na UI).
/// Negotiation de capabilities e MCP-over-ACP ficam como follow-up.
/// </summary>
public static class AcpEndpoints
{
    /// <summary>Maps <c>/api/acp/*</c> (auth: cookie ou sk-*).</summary>
    public static IEndpointRouteBuilder MapAcpEndpoints(
        this IEndpointRouteBuilder app)
    {
        var acp = app.MapGroup("/api/acp").WithTags("ACP")
            .RequireAuthorization();

        // session/new — cria conversa com prompt inicial e espera READY
        // (o run dispara via InitialMessage.Run, paridade do gateway).
        acp.MapPost("/conversations", async (
            JsonElement body, IAppConversationService convs,
            IStartTaskService tasks, HttpContext http,
            CancellationToken ct) =>
        {
            var prompt = ExtractPrompt(body);
            if (prompt is null or "")
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 400, "initial_prompt is required");
            }

            var task = await convs.CreateAsync(
                new CreateConversationRequest(
                    new InitialMessage("user",
                        [new { type = "text", text = prompt }]),
                    SelectedRepository: Prop(body, "selected_repository"),
                    AgentType: null,
                    LlmModel: Prop(body, "model"),
                    SandboxSpecId: null,
                    ConfirmationMode: false,
                    SandboxProfile: null,
                    Permissions: null,
                    Mode: null,
                    Secrets: null),
                ct);

            var convId = await WaitReadyAsync(tasks, task.Id, ct);
            return convId is null
                ? OpenHandsNetProblemFactory.Create(
                    http, 502, "start task failed")
                : Results.Created($"/api/acp/conversations/{convId}",
                    new { conversation_id = convId, state = "running" });
        });

        // session state — paridade GET de conversa, shape ACP.
        acp.MapGet("/conversations/{id:guid}", async (
            Guid id, IAppConversationService convs, HttpContext http,
            CancellationToken ct) =>
            await convs.GetAsync(id, ct) is { } c
                ? Results.Ok(new
                {
                    conversation_id = c.Id,
                    state = c.ExecutionStatus.ToString().ToLowerInvariant(),
                    title = c.Title,
                })
                : OpenHandsNetProblemFactory.Create(
                    http, 404, $"Conversation {id} not found"));

        // session/prompt — mensagem do usuário + acorda o runner.
        acp.MapPost("/conversations/{id:guid}/prompt", async (
            Guid id, JsonElement body, IAppConversationService convs,
            IEventService events, IAgentRunnerHost runnerHost,
            HttpContext http, CancellationToken ct) =>
        {
            var prompt = ExtractPrompt(body);
            if (prompt is null or "")
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 400, "prompt is required");
            }

            if (await convs.ValidateSendMessageAsync(id, ct) is { } err)
            {
                return OpenHandsNetProblemFactory.Create(http,
                err switch
                {
                    SendMessageError.NotFound => 404,
                    SendMessageError.Gone => 410,
                    SendMessageError.ServiceUnavailable => 503,
                    _ => 409,
                },
                $"conversation {id}: {err}");
            }

            await events.AppendAsync(id, new MessageEvent
            {
                Source = EventSource.User,
                LlmMessage = JsonSerializer.SerializeToElement(new
                {
                    role = "user",
                    content = new[]
                    {
                        new { type = "text", text = prompt },
                    },
                }, EventJson.Options),
            }, ct);
            await runnerHost.NotifyMessageAsync(id, ct);
            return Results.Accepted();
        });

        // session/cancel — interrompe o run corrente.
        acp.MapPost("/conversations/{id:guid}/cancel", async (
            Guid id, IAppConversationService convs,
            IAgentRunnerHost runnerHost, HttpContext http,
            CancellationToken ct) =>
        {
            if (await convs.GetAsync(id, ct) is null)
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 404, $"Conversation {id} not found");
            }

            await runnerHost.InterruptAsync(id, ct);
            return Results.NoContent();
        });

        // session/update — SSE com updates do turno (agent chunks +
        // turn_complete no estado terminal).
        acp.MapGet("/conversations/{id:guid}/events", async (
            Guid id, IAppConversationService convs,
            IEventService events, HttpContext http,
            CancellationToken ct) =>
        {
            if (await convs.GetAsync(id, ct) is null)
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 404, $"Conversation {id} not found");
            }

            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            try
            {
                await foreach (var e in events.SubscribeAsync(id, ct))
                {
                    var update = ToSessionUpdate(e);
                    if (update is null)
                    {
                        continue;
                    }

                    await http.Response.WriteAsync(
                        $"data: {JsonSerializer.Serialize(update)}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                    if (update.SessionUpdate == "turn_complete")
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // client desconectou — normal em SSE.
            }

            return Results.Empty;
        });

        return app;
    }

    /// <summary>Mapeia um evento durable p/ notificação session/update.</summary>
    private static AcpSessionUpdate? ToSessionUpdate(EventBase e) => e switch
    {
        MessageEvent { Source: EventSource.Agent } m => new(
            "agent_message_chunk",
            new { content = new { type = "text", text = ExtractText(m) } }),
        ConversationStateUpdateEvent s when s.Key == "execution_status"
            => new(StatusString(s.Value) is "finished" or "error"
                    or "stopped" ? "turn_complete" : "current_mode_update",
                new { status = StatusString(s.Value) }),
        _ => null,
    };

    /// <summary>Valor string de um state update (JsonElement?).</summary>
    private static string? StatusString(JsonElement? v) =>
        v is { ValueKind: JsonValueKind.String } e
            ? e.GetString()?.ToLowerInvariant() : null;

    /// <summary>session/update payload (shape ACP simplificado).</summary>
    private sealed record AcpSessionUpdate(
        [property: System.Text.Json.Serialization.JsonPropertyName("sessionUpdate")]
        string SessionUpdate,
        object? Payload = null);

    /// <summary>Extrai texto do LlmMessage do agente (string ou blocks).</summary>
    private static string ExtractText(MessageEvent m)
    {
        if (!m.LlmMessage.TryGetProperty("content", out var c))
        {
            return "";
        }

        return c.ValueKind switch
        {
            JsonValueKind.String => c.GetString() ?? "",
            JsonValueKind.Array => string.Concat(c.EnumerateArray()
                .Where(p => p.TryGetProperty("type", out var t)
                    && t.GetString() == "text")
                .Select(p => p.TryGetProperty("text", out var tx)
                    ? tx.GetString() : "")),
            _ => "",
        };
    }

    /// <summary>Prompt do body: string em <c>initial_prompt|prompt</c>
    /// ou blocks <c>content[]</c> estilo ACP.</summary>
    private static string? ExtractPrompt(JsonElement body)
    {
        foreach (var key in new[] { "initial_prompt", "prompt" })
        {
            if (body.TryGetProperty(key, out var p)
                && p.ValueKind == JsonValueKind.String)
            {
                return p.GetString();
            }
        }

        if (body.TryGetProperty("content", out var c)
            && c.ValueKind == JsonValueKind.Array)
        {
            var text = string.Concat(c.EnumerateArray()
                .Where(b => b.TryGetProperty("type", out var t)
                    && t.GetString() == "text")
                .Select(b => b.TryGetProperty("text", out var tx)
                    ? tx.GetString() : ""));
            return string.IsNullOrEmpty(text) ? null : text;
        }

        return null;
    }

    /// <summary>String property helper.</summary>
    private static string? Prop(JsonElement body, string key) =>
        body.TryGetProperty(key, out var p)
            && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    /// <summary>Espera o start-task chegar a READY; null em erro/fim.</summary>
    private static async Task<Guid?> WaitReadyAsync(
        IStartTaskService tasks, Guid taskId, CancellationToken ct)
    {
        await foreach (var update in tasks.WatchAsync(taskId, ct))
        {
            if (update.Status is StartTaskStatus.Ready)
            {
                return update.AppConversationId;
            }

            if (update.Status is StartTaskStatus.Error)
            {
                return null;
            }
        }

        return null;
    }
}