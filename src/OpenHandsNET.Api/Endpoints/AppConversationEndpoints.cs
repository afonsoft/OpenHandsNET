using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenHandsNET.Api.DTOs;
using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// <c>/api/v1/app-conversations</c> — paridade V1 (create assíncrono via
/// start-task, stream-start SSE, send-message com 404/409/410/503).
/// </summary>
public static class AppConversationEndpoints
{
    /// <summary>Maps the app-conversations route group.</summary>
    public static IEndpointRouteBuilder MapAppConversationEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/app-conversations").WithTags("Conversations");

        group.MapGet("/search", async (
            string? title__eq, DateTimeOffset? created_at__gte,
            string? page_id, int? limit,
            IAppConversationService svc, CancellationToken ct) =>
        {
            var page = await svc.SearchAsync(new ConversationQuery(
                title__eq, created_at__gte, page_id, limit ?? 100), ct);
            return Results.Ok(new
            {
                items = page.Items.Select(ToDto),
                next_page_id = page.NextPageId,
            });
        });

        group.MapGet("/count", async (
            string? title__eq, DateTimeOffset? created_at__gte,
            IAppConversationService svc, CancellationToken ct) =>
            Results.Ok(await svc.CountAsync(
                new ConversationQuery(title__eq, created_at__gte), ct)));

        group.MapGet("/", async (
            string ids, IAppConversationService svc, CancellationToken ct) =>
        {
            var parsed = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                .Where(g => g is not null).Select(g => g!.Value).ToList();
            var items = await svc.GetBatchAsync(parsed, ct);
            return Results.Ok(items.Select(ToDto));
        });

        group.MapGet("/{id:guid}", async (
            Guid id, IAppConversationService svc, HttpContext http,
            CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } c
                ? Results.Ok(ToDto(c))
                : OpenHandsNetProblemFactory.Create(http, 404, $"Conversation {id} not found"));

        group.MapPost("/", CreateAsync);
        group.MapPost("/stream-start", StreamStartAsync);

        group.MapPatch("/{id:guid}", async (
            Guid id, PatchConversationRequest body,
            IAppConversationService svc, HttpContext http, CancellationToken ct) =>
            await svc.UpdateAsync(id, body.Title, body.Tags, body.Pinned, ct) is { } c
                ? Results.Ok(ToDto(c))
                : OpenHandsNetProblemFactory.Create(http, 404, $"Conversation {id} not found"));

        group.MapDelete("/{id:guid}", async (
            Guid id, IAppConversationService svc, HttpContext http,
            CancellationToken ct) =>
            await svc.DeleteAsync(id, ct)
                ? Results.NoContent()
                : OpenHandsNetProblemFactory.Create(http, 404, $"Conversation {id} not found"));

        group.MapPost("/{id:guid}/send-message", SendMessageAsync);

        // Stubs upstream-parity: existem mas ainda não suportados.
        group.MapPost("/{id:guid}/switch_profile", (Guid id, HttpContext http) =>
            OpenHandsNetProblemFactory.Create(
                http, 501, "switch_profile not supported yet"));
        group.MapPost("/{id:guid}/switch_acp_model", (Guid id, HttpContext http) =>
            OpenHandsNetProblemFactory.Create(
                http, 501, "ACP model switching not supported yet"));

        // Proxies git/files foram para GitEndpoints (SPEC-12).
        group.MapGet("/{id:guid}/skills", () => Results.Ok(Array.Empty<object>()));
        group.MapGet("/{id:guid}/hooks", () => Results.Ok(Array.Empty<object>()));

        // start-tasks (polling).
        var tasks = app.MapGroup("/api/v1/app-conversations/start-tasks")
            .WithTags("Conversations");
        tasks.MapGet("/", BatchTasksAsync);
        tasks.MapGet("/search", SearchTasksAsync);
        tasks.MapGet("/count", async (
            string? status, IStartTaskService svc, CancellationToken ct) =>
            Results.Ok(await svc.CountAsync(ParseStatus(status), ct)));
        tasks.MapGet("/{id:guid}", async (
            Guid id, IStartTaskService svc, HttpContext http,
            CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } t
                ? Results.Ok(ToDto(t))
                : OpenHandsNetProblemFactory.Create(http, 404, $"Start task {id} not found"));

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateAppConversationRequest body,
        IAppConversationService svc, CancellationToken ct)
    {
        var task = await svc.CreateAsync(Map(body), ct);
        return Results.Created(
            $"/api/v1/app-conversations/start-tasks/{task.Id}", ToDto(task));
    }

    private static IResult StreamStartAsync(
        CreateAppConversationRequest body,
        IAppConversationService svc, IStartTaskService tasks,
        CancellationToken ct)
    {
        // SSE: um item por transição de status até READY|ERROR.
        return TypedResults.ServerSentEvents(StreamTaskUpdates(body, svc, tasks, ct));
    }

    private static async IAsyncEnumerable<
        System.Net.ServerSentEvents.SseItem<StartTaskDto>> StreamTaskUpdates(
        CreateAppConversationRequest body,
        IAppConversationService svc, IStartTaskService tasks,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var task = await svc.CreateAsync(Map(body), ct);
        yield return new System.Net.ServerSentEvents.SseItem<StartTaskDto>(ToDto(task));
        await foreach (var update in tasks.WatchAsync(task.Id, ct))
        {
            yield return new System.Net.ServerSentEvents.SseItem<StartTaskDto>(
                ToDto(update));
        }
    }

    private static async Task<IResult> SendMessageAsync(
        Guid id, SendMessageRequest body,
        IAppConversationService svc, IEventService events,
        OpenHandsNET.Application.Agent.IAgentRunnerHost runnerHost,
        HttpContext http, CancellationToken ct)
    {
        var error = await svc.ValidateSendMessageAsync(id, ct);
        if (error is not null)
        {
            return error switch
            {
                SendMessageError.NotFound => OpenHandsNetProblemFactory.Create(
                    http, 404, $"Conversation {id} not found"),
                SendMessageError.Gone => OpenHandsNetProblemFactory.Create(
                    http, 410, OpenHandsNetProblemFactory.ConversationGone),
                SendMessageError.ServiceUnavailable => OpenHandsNetProblemFactory.Create(
                    http, 503, OpenHandsNetProblemFactory.SandboxError),
                _ => OpenHandsNetProblemFactory.Create(
                    http, 409, OpenHandsNetProblemFactory.SandboxNotRunning),
            };
        }

        // Mensagem do usuário entra no stream como MessageEvent (V1).
        var content = body.Content.Select(el => (object)el).ToList();
        var message = new MessageEvent
        {
            Source = EventSource.User,
            LlmMessage = JsonSerializer.SerializeToElement(new
            {
                role = body.Role,
                content,
            }, EventJson.Options),
        };
        await events.AppendAsync(id, message, ct);
        if (body.Run)
        {
            await runnerHost.NotifyMessageAsync(id, ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> BatchTasksAsync(
        string ids, IStartTaskService svc, CancellationToken ct)
    {
        var parsed = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
            .Where(g => g is not null).Select(g => g!.Value).ToList();
        return Results.Ok((await svc.GetBatchAsync(parsed, ct)).Select(ToDto));
    }

    private static async Task<IResult> SearchTasksAsync(
        string? status, string? page_id, int? limit,
        IStartTaskService svc, CancellationToken ct) =>
        Results.Ok((await svc.SearchAsync(
                ParseStatus(status), page_id, limit ?? 100, ct))
            .Select(ToDto));

    private static CreateConversationRequest Map(CreateAppConversationRequest b) =>
        new(b.InitialMessage is null ? null : new InitialMessage(
                b.InitialMessage.Role,
                b.InitialMessage.Content.Cast<object>().ToList(),
                b.InitialMessage.Run),
            b.SelectedRepository, b.AgentType, b.LlmModel, b.SandboxSpecId,
            b.ConfirmationMode);

    private static StartTaskStatus? ParseStatus(string? s) =>
        Enum.TryParse<StartTaskStatus>(s, ignoreCase: true, out var v) ? v : null;

    private static StartTaskDto ToDto(AppConversationStartTask t) => new()
    {
        Id = t.Id.ToString(), Status = t.Status.ToString().ToUpperInvariant(),
        AppConversationId = t.AppConversationId?.ToString(),
        SandboxId = t.SandboxId?.ToString(), Detail = t.Detail,
        CreatedAt = t.CreatedAt, UpdatedAt = t.UpdatedAt,
    };

    private static AppConversationDto ToDto(AppConversation c) => new()
    {
        Id = c.Id.ToString(), Title = c.Title, SandboxId = c.SandboxId?.ToString(),
        SelectedRepository = c.SelectedRepository, AgentType = c.AgentType,
        LlmModel = c.LlmModel,
        ExecutionStatus = c.ExecutionStatus.ToString().ToUpperInvariant(),
        Tags = c.Tags, Pinned = c.Pinned, ConfirmationMode = c.ConfirmationMode,
        CreatedAt = c.CreatedAt, UpdatedAt = c.UpdatedAt,
    };
}
