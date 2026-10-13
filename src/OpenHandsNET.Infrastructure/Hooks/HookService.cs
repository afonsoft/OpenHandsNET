using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Application.Hooks;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Domain.Hooks;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Hooks;

/// <summary>
/// Lifecycle hook execution (SPEC-11 RF-004): resolves enabled hook configs
/// for a point and runs emit_event / run_command (inside the sandbox) /
/// webhook (POST with retry). Always appends a <see cref="HookExecutionEvent"/>;
/// failures never abort the conversation unless <c>blocking</c> is set.
/// </summary>
public sealed class HookService(
    AppDbContext db,
    IEventService events,
    IHttpClientFactory httpClientFactory,
    ILogger<HookService> logger) : IHookService
{
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)];

    /// <inheritdoc />
    public async Task<HookConfig> CreateAsync(HookConfig config, CancellationToken ct)
    {
        var record = new HookConfigRecord
        {
            Id = config.Id == Guid.Empty ? Guid.NewGuid() : config.Id,
            OwnerId = config.OwnerId,
            OrgId = config.OrgId,
            Name = config.Name,
            Event = EventName(config.Event),
            Action = ActionName(config.Action),
            Payload = config.Payload,
            Blocking = config.Blocking,
            Enabled = config.Enabled,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.HookConfigs.Add(record);
        await db.SaveChangesAsync(ct);
        return ToDomain(record);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HookConfig>> ListAsync(
        string? ownerId, CancellationToken ct) =>
        (await db.HookConfigs.AsNoTracking().ToListAsync(ct))
            .Where(r => r.OwnerId is null || r.OwnerId == ownerId)
            .Select(ToDomain)
            .ToList();

    /// <inheritdoc />
    public async Task<HookConfig?> UpdateAsync(
        Guid id, string? ownerId, UpdateHookRequest update, CancellationToken ct)
    {
        var r = await db.HookConfigs.FindAsync([id], ct);
        if (r is null || (r.OwnerId is not null && r.OwnerId != ownerId))
        {
            return null;
        }

        if (update.Name is not null)
        {
            r.Name = update.Name;
        }

        if (update.Event is { } e)
        {
            r.Event = EventName(e);
        }

        if (update.Action is { } a)
        {
            r.Action = ActionName(a);
        }

        if (update.Payload is not null)
        {
            r.Payload = update.Payload;
        }

        if (update.Blocking is { } b)
        {
            r.Blocking = b;
        }

        if (update.Enabled is { } en)
        {
            r.Enabled = en;
        }

        await db.SaveChangesAsync(ct);
        return ToDomain(r);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, string? ownerId, CancellationToken ct)
    {
        var r = await db.HookConfigs.FindAsync([id], ct);
        if (r is null || (r.OwnerId is not null && r.OwnerId != ownerId))
        {
            return false;
        }

        db.HookConfigs.Remove(r);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RunAsync(
        HookEvent point, HookContext context, CancellationToken ct)
    {
        var hooks = (await db.HookConfigs.AsNoTracking()
                .Where(h => h.Enabled && h.Event == EventName(point))
                .ToListAsync(ct))
            .Select(ToDomain)
            .ToList();
        if (hooks.Count == 0)
        {
            return false;
        }

        var blocked = false;
        foreach (var hook in hooks)
        {
            try
            {
                var ev = await ExecuteAsync(hook, context, ct);
                await events.AppendAsync(context.ConversationId, ev,
                    CancellationToken.None);
                if (hook.Blocking && !ev.Success)
                {
                    blocked = true;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Hook {Hook} failed at {Point}",
                    hook.Name, point);
                await events.AppendAsync(context.ConversationId,
                    new HookExecutionEvent
                    {
                        HookEventType = EventName(point),
                        HookCommand = hook.Name,
                        ToolName = context.ToolName,
                        ActionId = context.ActionId,
                        MessageId = context.MessageId,
                        Success = false,
                        Blocked = hook.Blocking,
                        Error = SecretRedactor.Redact(ex.Message),
                        HookInput = context.HookInput,
                    }, CancellationToken.None);
                if (hook.Blocking)
                {
                    blocked = true;
                }
            }
        }

        return blocked;
    }

    private async Task<HookExecutionEvent> ExecuteAsync(
        HookConfig hook, HookContext context, CancellationToken ct)
    {
        var success = false;
        var exitCode = 0;
        var stdout = "";
        var stderr = "";
        string? error = null;
        string? additionalContext = null;
        var messageId = context.MessageId;

        switch (hook.Action)
        {
            case HookAction.EmitEvent:
                success = true;
                additionalContext = hook.Payload;
                if (!string.IsNullOrEmpty(hook.Payload))
                {
                    var msg = await events.AppendAsync(context.ConversationId,
                        new MessageEvent
                        {
                            Source = EventSource.Hook,
                            LlmMessage = JsonSerializer.SerializeToElement(new
                            {
                                role = "user",
                                content = new[]
                                {
                                    new { type = "text", text = hook.Payload },
                                },
                            }, EventJson.Options),
                        }, CancellationToken.None);
                    messageId ??= msg.Id;
                }

                break;

            case HookAction.RunCommand:
                {
                    var exec = context.Services?
                        .GetService(typeof(ISandboxExecutor)) as ISandboxExecutor;
                    if (context.Sandbox is null || exec is null ||
                        string.IsNullOrEmpty(hook.Payload))
                    {
                        error = "run_command requires a running sandbox and a command";
                        break;
                    }

                    var result = await exec.ExecAsync(
                        context.Sandbox, hook.Payload,
                        TimeSpan.FromSeconds(60), ct);
                    success = result.ExitCode == 0;
                    exitCode = result.ExitCode;
                    stdout = result.StdOut;
                    stderr = SecretRedactor.Redact(result.StdErr);
                    if (!success)
                    {
                        error = $"exit_code={result.ExitCode}";
                    }
                }

                break;

            case HookAction.Webhook:
                if (string.IsNullOrEmpty(hook.Payload))
                {
                    error = "webhook hook requires a target URL payload";
                    break;
                }

                success = await PostWithRetryAsync(hook.Payload, context, ct);
                if (!success)
                {
                    error = "webhook delivery failed";
                }

                break;
        }

        return new HookExecutionEvent
        {
            HookEventType = EventName(hook.Event),
            HookCommand = hook.Payload ?? hook.Name,
            ToolName = context.ToolName,
            ActionId = context.ActionId,
            MessageId = messageId,
            Success = success,
            ExitCode = exitCode,
            Stdout = stdout,
            Stderr = stderr,
            Error = error,
            AdditionalContext = additionalContext,
            HookInput = context.HookInput,
        };
    }

    private async Task<bool> PostWithRetryAsync(
        string url, HookContext context, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("webhooks");
        var body = JsonSerializer.Serialize(new
        {
            conversation_id = context.ConversationId,
            tool = context.ToolName,
            action_id = context.ActionId,
            message_id = context.MessageId,
            at = DateTimeOffset.UtcNow,
        });
        foreach (var delay in Backoff.Append(TimeSpan.Zero))
        {
            try
            {
                using var resp = await client.PostAsync(url,
                    new StringContent(body, Encoding.UTF8, "application/json"), ct);
                if (resp.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Hook webhook {Url} failed", url);
            }

            await Task.Delay(delay, CancellationToken.None);
        }

        return false;
    }

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

    private static HookConfig ToDomain(HookConfigRecord r) => new()
    {
        Id = r.Id,
        OwnerId = r.OwnerId,
        OrgId = r.OrgId,
        Name = r.Name,
        Event = r.Event switch
        {
            "on_conversation_start" => HookEvent.OnConversationStart,
            "on_message" => HookEvent.OnMessage,
            "on_tool_pre" => HookEvent.OnToolPre,
            "on_tool_post" => HookEvent.OnToolPost,
            _ => HookEvent.OnFinish,
        },
        Action = r.Action switch
        {
            "emit_event" => HookAction.EmitEvent,
            "run_command" => HookAction.RunCommand,
            _ => HookAction.Webhook,
        },
        Payload = r.Payload,
        Blocking = r.Blocking,
        Enabled = r.Enabled,
        CreatedAt = r.CreatedAt,
    };
}
