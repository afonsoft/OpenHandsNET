using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Application.Hooks;
using OpenHandsNET.Domain.Hooks;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// <c>/api/v1/hooks</c> CRUD + hooks/execuções da conversa (SPEC-11 RF-004,
/// API Contract §5).
/// </summary>
public static class HookEndpoints
{
    /// <summary>Maps hook endpoints.</summary>
    public static IEndpointRouteBuilder MapHookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/hooks", ListAsync).WithTags("Hooks");
        app.MapPost("/api/v1/hooks", CreateAsync).WithTags("Hooks");
        app.MapPatch("/api/v1/hooks/{id:guid}", UpdateAsync).WithTags("Hooks");
        app.MapDelete("/api/v1/hooks/{id:guid}", DeleteAsync).WithTags("Hooks");
        app.MapGet("/api/v1/app-conversations/{id:guid}/hooks", ForConversationAsync)
            .WithTags("Hooks");
        return app;
    }

    private static async Task<IResult> ListAsync(
        IHookService hooks, CancellationToken ct) =>
        Results.Ok((await hooks.ListAsync(null, ct)).Select(ToDto).ToList());

    private static async Task<IResult> CreateAsync(
        HookDto req, IHookService hooks, HttpContext http, CancellationToken ct)
    {
        if (!TryParseEvent(req.Event, out var ev) ||
            !TryParseAction(req.Action, out var action))
        {
            return OpenHandsNetProblemFactory.Create(
                http, 400, "invalid event or action");
        }

        var hook = await hooks.CreateAsync(new HookConfig
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            Event = ev,
            Action = action,
            Payload = req.Payload,
            Blocking = req.Blocking,
        }, ct);
        return Results.Created($"/api/v1/hooks/{hook.Id}", ToDto(hook));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, HookDto req, IHookService hooks,
        HttpContext http, CancellationToken ct)
    {
        HookEvent? ev = req.Event is null ? null :
            TryParseEvent(req.Event, out var e) ? e : (HookEvent?)null;
        if (req.Event is not null && ev is null)
        {
            return OpenHandsNetProblemFactory.Create(http, 400, "invalid event");
        }

        HookAction? action = req.Action is null ? null :
            TryParseAction(req.Action, out var a) ? a : (HookAction?)null;
        if (req.Action is not null && action is null)
        {
            return OpenHandsNetProblemFactory.Create(http, 400, "invalid action");
        }

        var updated = await hooks.UpdateAsync(id, null,
            new UpdateHookRequest(req.Name, ev, action, req.Payload,
                req.Blocking, req.Enabled), ct);
        return updated is { } h
            ? Results.Ok(ToDto(h))
            : OpenHandsNetProblemFactory.Create(http, 404, "hook not found");
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, IHookService hooks, HttpContext http, CancellationToken ct) =>
        await hooks.DeleteAsync(id, null, ct)
            ? Results.NoContent()
            : OpenHandsNetProblemFactory.Create(http, 404, "hook not found");

    /// <summary>Hooks configurados + últimas execuções da conversa.</summary>
    private static async Task<IResult> ForConversationAsync(
        Guid id, IHookService hooks, IEventService events,
        CancellationToken ct)
    {
        var configs = (await hooks.ListAsync(null, ct))
            .Select(ToDto).ToList();
        var page = await events.SearchAsync(id,
            new EventQuery(Limit: 100), ct);
        var executions = page.Items
            .OfType<Domain.Events.HookExecutionEvent>()
            .Select(e => new
            {
                id = e.Id,
                hook_name = e.HookCommand,
                @event = e.HookEventType,
                success = e.Success,
                exit_code = e.ExitCode,
                error = e.Error,
                timestamp = e.Timestamp,
            }).ToList();
        return Results.Ok(new { hooks = configs, executions });
    }

    private static bool TryParseEvent(string? name, out HookEvent e)
    {
        e = name switch
        {
            "on_conversation_start" => HookEvent.OnConversationStart,
            "on_message" => HookEvent.OnMessage,
            "on_tool_pre" => HookEvent.OnToolPre,
            "on_tool_post" => HookEvent.OnToolPost,
            "on_finish" => HookEvent.OnFinish,
            _ => HookEvent.OnConversationStart,
        };
        return name is "on_conversation_start" or "on_message" or "on_tool_pre"
            or "on_tool_post" or "on_finish";
    }

    private static bool TryParseAction(string? name, out HookAction a)
    {
        a = name switch
        {
            "emit_event" => HookAction.EmitEvent,
            "run_command" => HookAction.RunCommand,
            _ => HookAction.Webhook,
        };
        return name is "emit_event" or "run_command" or "webhook";
    }

    private static object ToDto(HookConfig h) => new
    {
        id = h.Id,
        name = h.Name,
        @event = EventName(h.Event),
        action = ActionName(h.Action),
        payload = h.Payload,
        blocking = h.Blocking,
        enabled = h.Enabled,
        created_at = h.CreatedAt,
    };

    private static string EventName(HookEvent e) => e switch
    {
        HookEvent.OnConversationStart => "on_conversation_start",
        HookEvent.OnMessage => "on_message",
        HookEvent.OnToolPre => "on_tool_pre",
        HookEvent.OnToolPost => "on_tool_post",
        _ => "on_finish",
    };

    private static string ActionName(HookAction a) => a switch
    {
        HookAction.EmitEvent => "emit_event",
        HookAction.RunCommand => "run_command",
        _ => "webhook",
    };

    /// <summary>Hook config DTO (create/update).</summary>
    public sealed record HookDto(
        string Name, string? Event, string? Action,
        string? Payload, bool Blocking, bool? Enabled);
}
