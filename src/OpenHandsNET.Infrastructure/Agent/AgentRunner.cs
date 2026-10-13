using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Agent;

/// <summary>
/// ReAct loop for one conversation (SPEC-09 RF-002): consumes the event stream
/// (single source of truth), streams the LLM, dispatches tool calls to the
/// sandbox and maps everything back to V1 events. Exits on
/// finish/interrupt/pause/error/stuck.
/// </summary>
public sealed class AgentRunner(
    Guid conversationId,
    IServiceProvider services,
    ILogger<AgentRunner> logger) : IAgentRunner
{
    private readonly Channel<MessageEvent> _incoming =
        Channel.CreateUnbounded<MessageEvent>();
    private readonly Dictionary<string, TaskCompletionSource<bool>> _confirms = new();
    private volatile bool _pauseRequested;
    private volatile bool _interruptSeen;

    /// <summary>Routes a live event into the loop (user msg/interrupt/pause).</summary>
    public void OnEvent(EventBase e)
    {
        switch (e)
        {
            case PauseEvent:
                _pauseRequested = true;
                _interruptSeen = true;
                break;
            case InterruptEvent:
                _interruptSeen = true;
                break;
            case MessageEvent { Source: EventSource.User } m:
                _incoming.Writer.TryWrite(m);
                break;
        }
    }

    /// <summary>Completes a pending confirmation wait.</summary>
    public void ResolveConfirmation(string actionId, bool approved)
    {
        if (_confirms.Remove(actionId, out var tcs))
        {
            tcs.TrySetResult(approved);
        }
    }

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var events = services.GetRequiredService<IEventService>();
        var sandboxes = services.GetRequiredService<ISandboxService>();
        var executor = services.GetRequiredService<ISandboxExecutor>();
        var tools = services.GetRequiredService<IToolRegistry>();
        var llm = services.GetRequiredService<IAgentLlmProvider>();
        var condenser = services.GetRequiredService<ICondenser>();
        var opt = services.GetRequiredService<IOptions<AgentLlmOptions>>().Value;

        var conv = await db.Conversations.FindAsync([conversationId], ct);
        if (conv is null)
        {
            return;
        }

        var sandbox = conv.SandboxId is null
            ? null
            : await sandboxes.GetAsync(conv.SandboxId.Value, ct);
        if (sandbox?.Status is not Domain.Sandbox.SandboxStatus.Running)
        {
            await events.AppendAsync(conversationId, new ServerErrorEvent
            {
                Code = "sandbox_not_running",
                Detail = "Agent runner requires a RUNNING sandbox.",
            }, CancellationToken.None);
            return;
        }

        await SetStatusAsync(events, db, conv, "running",
            ConversationExecutionStatus.Running, ct);
        using var pump = Subscribe(events);

        // SPEC-11: hooks de ciclo de vida — falhas não abortam (salvo blocking).
        var hooks = services.GetService<Application.Hooks.IHookService>();
        if (hooks is not null)
        {
            await hooks.RunAsync(Domain.Hooks.HookEvent.OnConversationStart,
                new Application.Hooks.HookContext
                {
                    ConversationId = conversationId,
                    Sandbox = sandbox,
                    Services = services,
                }, ct);
        }

        try
        {
            await LoopAsync(events, db, conv, sandbox, executor, tools, llm,
                condenser, opt, ct);
        }
        catch (OperationCanceledException)
        {
            await SetStatusAsync(events, db, conv,
                _pauseRequested ? "paused" : "idle",
                _pauseRequested
                    ? ConversationExecutionStatus.Paused
                    : ConversationExecutionStatus.Running,
                CancellationToken.None);
        }
    }

    private async Task LoopAsync(
        IEventService events, AppDbContext db, ConversationRecord conv,
        Domain.Sandbox.Sandbox sandbox, ISandboxExecutor executor,
        IToolRegistry tools, IAgentLlmProvider llm, ICondenser condenser,
        AgentLlmOptions opt, CancellationToken ct)
    {
        var (history, lastEventId) = await BuildHistoryAsync(
            events, tools, conv, ct);
        var confirmationMode = conv.ConfirmationMode;
        var hooks = services.GetService<Application.Hooks.IHookService>();

        for (var iteration = 0; iteration < opt.MaxIterations; iteration++)
        {
            using var iterSpan = Telemetry.OpenNetTelemetry.StartSpan(
                "agent_iteration", conversationId, sandbox.Id.ToString());
            iterSpan?.SetTag("iteration", iteration);
            if (await CheckSignalsAsync(events, db, conv))
            {
                return;
            }

            // pending_messages: usuário falou durante o run → entra no histórico.
            while (_incoming.Reader.TryRead(out var pending))
            {
                history.Add(ToLlmMessage(pending));
                lastEventId = pending.Id;
                if (hooks is not null)
                {
                    await hooks.RunAsync(Domain.Hooks.HookEvent.OnMessage,
                        new Application.Hooks.HookContext
                        {
                            ConversationId = conversationId,
                            Sandbox = sandbox,
                            Services = services,
                            MessageId = pending.Id,
                        }, ct);
                }
            }

            var request = await condenser.CondenseAsync(history, ct);
            var toolSchemas = tools.All.Select(t => t.Schema).ToList();

            var text = new System.Text.StringBuilder();
            var calls = new SortedDictionary<int, (string id, string name,
                System.Text.StringBuilder args)>();
            string? finishReason = null;
            var responseId = "";
            try
            {
                using var llmSpan = Telemetry.OpenNetTelemetry.StartSpan(
                    "llm_call", conversationId, sandbox.Id.ToString());
                llmSpan?.SetTag("model", opt.Model);
                await foreach (var chunk in llm.ChatStreamAsync(
                    request, toolSchemas, new LlmOptions(), ct))
                {
                    switch (chunk)
                    {
                        case TextDelta d:
                            text.Append(d.Text);
                            lastEventId = (await events.AppendAsync(conversationId,
                                new StreamingDeltaEvent
                                {
                                    Content = d.Text,
                                    ParentId = lastEventId,
                                }, ct)).Id;
                            break;
                        case ToolCallDelta tcd:
                            if (!calls.TryGetValue(tcd.Index, out var cur))
                            {
                                cur = ("", "", new System.Text.StringBuilder());
                            }

                            cur.id = tcd.Id ?? cur.id;
                            cur.name = tcd.Name ?? cur.name;
                            if (tcd.ArgumentsFragment is { } frag)
                            {
                                cur.args.Append(frag);
                            }

                            calls[tcd.Index] = cur;
                            break;
                        case CompletionEnd end:
                            finishReason = end.FinishReason;
                            responseId = end.ResponseId;
                            await events.AppendAsync(conversationId, new TokenEvent
                            {
                                Source = EventSource.Agent,
                                ParentId = lastEventId,
                                PromptTokens = end.Usage.PromptTokens,
                                CompletionTokens = end.Usage.CompletionTokens,
                                CostUsd = end.Usage.CostUsd,
                            }, ct);
                            Telemetry.OpenNetTelemetry.LlmTokens
                                .WithLabels("prompt").Inc(end.Usage.PromptTokens);
                            Telemetry.OpenNetTelemetry.LlmTokens
                                .WithLabels("completion").Inc(end.Usage.CompletionTokens);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "LLM completion failed");
                await events.AppendAsync(conversationId, new AgentErrorEvent
                {
                    ToolName = "llm", ToolCallId = "",
                    Error = SecretRedactor.Redact(ex.Message),
                }, CancellationToken.None);
                await SetStatusAsync(events, db, conv, "error",
                    ConversationExecutionStatus.Error, CancellationToken.None);
                return;
            }

            var toolCalls = calls.Select(kv => new LlmToolCall(
                kv.Value.id is { Length: > 0 } i ? i : $"call_{responseId}_{kv.Key}",
                kv.Value.name, kv.Value.args.ToString())).ToList();

            if (toolCalls.Count == 0)
            {
                if (text.Length == 0)
                {
                    // Completion vazio → nada a fazer; encerra em idle.
                    await SetStatusAsync(events, db, conv, "idle",
                        ConversationExecutionStatus.Running, CancellationToken.None);
                    return;
                }

                var msg = new MessageEvent
                {
                    Source = EventSource.Agent,
                    ParentId = lastEventId,
                    LlmResponseId = responseId,
                    LlmMessage = JsonSerializer.SerializeToElement(new
                    {
                        role = "assistant",
                        content = new[]
                        {
                            new { type = "text", text = text.ToString() },
                        },
                    }, EventJson.Options),
                };
                lastEventId = (await events.AppendAsync(
                    conversationId, msg, ct)).Id;
                history.Add(LlmMessage.Assistant(text.ToString()));
                continue;
            }

            history.Add(LlmMessage.AssistantToolCall(
                text.Length > 0 ? text.ToString() : null, toolCalls));

            foreach (var call in toolCalls)
            {
                var actionEvent = new ActionEvent
                {
                    ParentId = lastEventId,
                    ToolName = call.Name,
                    ToolCallId = call.Id,
                    LlmResponseId = responseId,
                    Action = TryParseJson(call.ArgumentsJson),
                    Thought = text.Length > 0
                        ? JsonSerializer.SerializeToElement(
                            new[] { new { type = "text", text = text.ToString() } },
                            EventJson.Options)
                        : null,
                };
                lastEventId = (await events.AppendAsync(
                    conversationId, actionEvent, ct)).Id;

                var tool = tools.Find(call.Name);
                if (tool is null)
                {
                    var obsId = await ObserveAsync(events, conversationId,
                        actionEvent,
                        ToolResult.Error($"unknown tool '{call.Name}'"), ct);
                    history.Add(LlmMessage.ToolResult(
                        call.Id, $"{{\"error\":\"unknown tool '{call.Name}'\"}}"));
                    lastEventId = obsId;
                    continue;
                }

                // SPEC-11: on_tool_pre — hook blocking pode vetar a ação.
                if (hooks is not null && await hooks.RunAsync(
                        Domain.Hooks.HookEvent.OnToolPre,
                        new Application.Hooks.HookContext
                        {
                            ConversationId = conversationId,
                            Sandbox = sandbox,
                            Services = services,
                            ToolName = call.Name,
                            ActionId = actionEvent.Id,
                            HookInput = actionEvent.Action,
                        }, ct))
                {
                    var vetoId = await ObserveAsync(events, conversationId,
                        actionEvent,
                        ToolResult.Error("blocked by hook"), ct);
                    history.Add(LlmMessage.ToolResult(
                        call.Id, "{\"error\":\"blocked by hook\"}"));
                    lastEventId = vetoId;
                    continue;
                }

                if (tool.Risky && confirmationMode)
                {
                    await SetStatusAsync(events, db, conv,
                        "waiting_for_confirmation", null, ct,
                        pendingAction: actionEvent.Id);
                    var approved = await WaitConfirmationAsync(
                        events, db, conv, actionEvent.Id, opt, ct);
                    if (approved is null)
                    {
                        return; // timeout/interrupt já sinalizados
                    }

                    if (!approved.Value)
                    {
                        history.Add(LlmMessage.ToolResult(
                            call.Id, "User rejected this action."));
                        continue;
                    }

                    await SetStatusAsync(events, db, conv, "running",
                        ConversationExecutionStatus.Running, ct);
                }

                ToolResult result;
                using var toolSpan = Telemetry.OpenNetTelemetry.StartSpan(
                    "tool_call", conversationId, sandbox.Id.ToString());
                toolSpan?.SetTag("tool", call.Name);
                Telemetry.OpenNetTelemetry.ToolCalls.WithLabels(call.Name).Inc();
                try
                {
                    using var toolCts =
                        CancellationTokenSource.CreateLinkedTokenSource(ct);
                    toolCts.CancelAfter(
                        TimeSpan.FromSeconds(opt.ToolTimeoutSeconds));
                    var ctx = new ToolCallContext(conversationId, sandbox,
                        executor, events, services,
                        TimeSpan.FromSeconds(opt.ToolTimeoutSeconds));
                    result = await tool.Handler(ctx,
                        TryParseJson(call.ArgumentsJson)
                            ?? JsonDocument.Parse("{}").RootElement.Clone(),
                        toolCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    result = ToolResult.Error("tool timed out");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = ToolResult.Error(SecretRedactor.Redact(ex.Message));
                }

                var observationId = await ObserveAsync(
                    events, conversationId, actionEvent, result, ct);
                history.Add(LlmMessage.ToolResult(
                    call.Id, result.Observation.GetRawText()));

                // SPEC-11: on_tool_post — auditoria após a observação.
                if (hooks is not null)
                {
                    await hooks.RunAsync(Domain.Hooks.HookEvent.OnToolPost,
                        new Application.Hooks.HookContext
                        {
                            ConversationId = conversationId,
                            Sandbox = sandbox,
                            Services = services,
                            ToolName = call.Name,
                            ActionId = actionEvent.Id,
                        }, ct);
                }

                if (call.Name == "finish")
                {
                    var finalText = result.Observation.TryGetProperty(
                        "final_message", out var fm) ? fm.GetString() : null;
                    if (!string.IsNullOrEmpty(finalText))
                    {
                        await events.AppendAsync(conversationId, new MessageEvent
                        {
                            Source = EventSource.Agent,
                            ParentId = observationId,
                            LlmResponseId = responseId,
                            LlmMessage = JsonSerializer.SerializeToElement(new
                            {
                                role = "assistant",
                                content = new[]
                                {
                                    new { type = "text", text = finalText },
                                },
                            }, EventJson.Options),
                        }, ct);
                    }

                    await SetStatusAsync(events, db, conv, "finished",
                        ConversationExecutionStatus.Finished, ct);
                    if (hooks is not null)
                    {
                        await hooks.RunAsync(Domain.Hooks.HookEvent.OnFinish,
                            new Application.Hooks.HookContext
                            {
                                ConversationId = conversationId,
                                Sandbox = sandbox,
                                Services = services,
                                ToolName = call.Name,
                                ActionId = actionEvent.Id,
                            }, CancellationToken.None);
                    }

                    return;
                }

                lastEventId = observationId;
            }
        }

        await events.AppendAsync(conversationId, new AgentErrorEvent
        {
            ToolName = "loop", ToolCallId = "",
            Error = $"max_iterations ({opt.MaxIterations}) reached",
        }, CancellationToken.None);
        await SetStatusAsync(events, db, conv, "stuck",
            ConversationExecutionStatus.Error, CancellationToken.None);
    }

    /// <summary>Interrupt/pause → status de saída; true = encerrar o loop.</summary>
    private async Task<bool> CheckSignalsAsync(
        IEventService events, AppDbContext db, ConversationRecord conv)
    {
        if (!_interruptSeen)
        {
            return false;
        }

        await SetStatusAsync(events, db, conv,
            _pauseRequested ? "paused" : "idle",
            _pauseRequested
                ? ConversationExecutionStatus.Paused
                : ConversationExecutionStatus.Running,
            CancellationToken.None);
        return true;
    }

    private async Task<bool?> WaitConfirmationAsync(
        IEventService events, AppDbContext db, ConversationRecord conv,
        string actionId, AgentLlmOptions opt, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _confirms[actionId] = tcs;
        try
        {
            using var timeout = CancellationTokenSource
                .CreateLinkedTokenSource(ct);
            timeout.CancelAfter(
                TimeSpan.FromMinutes(opt.ConfirmationTimeoutMinutes));
            return await tcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            await events.AppendAsync(conversationId, new InterruptEvent(),
                CancellationToken.None);
            await SetStatusAsync(events, db, conv,
                _pauseRequested ? "paused" : "idle",
                _pauseRequested
                    ? ConversationExecutionStatus.Paused
                    : ConversationExecutionStatus.Running,
                CancellationToken.None);
            return null;
        }
    }

    /// <summary>Pump: SubscribeAsync → OnEvent (eventos são a fonte da verdade).</summary>
    private IDisposable Subscribe(IEventService events)
    {
        var pumpCts = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in events.SubscribeAsync(
                    conversationId, pumpCts.Token))
                {
                    OnEvent(e);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
        return new PumpHandle(pumpCts, pump);
    }

    private sealed class PumpHandle(
        CancellationTokenSource cts, Task pump) : IDisposable
    {
        public void Dispose()
        {
            cts.Cancel();
            try
            {
                pump.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            cts.Dispose();
        }
    }

    /// <summary>Reconstrói o histórico LLM a partir do stream (resume).</summary>
    private async Task<(List<LlmMessage> history, string? lastEventId)>
        BuildHistoryAsync(IEventService events, IToolRegistry tools,
            ConversationRecord conv, CancellationToken ct)
    {
        var all = new List<EventBase>();
        string? cursor = null;
        do
        {
            var page = await events.SearchAsync(conversationId,
                new EventQuery(PageId: cursor, Limit: 100), ct);
            all.AddRange(page.Items);
            cursor = page.NextPageId;
        }
        while (cursor is not null);

        var forgotten = all.OfType<Condensation>()
            .SelectMany(c => c.ForgottenEventIds).ToHashSet();
        var history = new List<LlmMessage>();
        string? lastId = null;
        var hasSystem = false;
        foreach (var e in all)
        {
            if (forgotten.Contains(e.Id))
            {
                continue;
            }

            lastId = e.Id;
            switch (e)
            {
                case SystemPromptEvent sp:
                    hasSystem = true;
                    history.Add(new LlmMessage("system", Flatten(sp.SystemPrompt)));
                    break;
                case MessageEvent m:
                    history.Add(ToLlmMessage(m));
                    break;
                case ActionEvent a:
                    history.Add(LlmMessage.AssistantToolCall(
                        Flatten(a.Thought),
                        [new LlmToolCall(a.ToolCallId, a.ToolName,
                            a.Action?.GetRawText() ?? "{}")]));
                    break;
                case ObservationEvent o:
                    history.Add(LlmMessage.ToolResult(
                        o.ToolCallId, o.Observation.GetRawText()));
                    break;
                case UserRejectObservation r:
                    history.Add(LlmMessage.ToolResult(
                        r.ToolCallId, $"User rejected: {r.RejectionReason}"));
                    break;
            }
        }

        if (!hasSystem)
        {
            // SPEC-11 RF-002: resolve skills por trigger (always/keyword/task)
            // usando a última mensagem do usuário e as tags da conversa.
            var lastUserText = all.OfType<MessageEvent>()
                .Where(m => m.Source == EventSource.User)
                .Select(m => ToLlmMessage(m).Content)
                .LastOrDefault();
            var skills = services.GetService<Application.Skills.ISkillService>()
                is { } skillSvc
                ? await skillSvc.ResolveForConversationAsync(
                    conversationId, lastUserText, null,
                    conv.CreatedByUserId, conv.OrgId?.ToString(), ct)
                : [];
            var prompt = SystemPromptBuilder.Build(
                conv.SelectedRepository,
                tools.All.Select(t => t.Schema).ToList(),
                skills);
            var sp = new SystemPromptEvent
            {
                ParentId = lastId,
                SystemPrompt = JsonSerializer.SerializeToElement(
                    new[] { new { type = "text", text = prompt } },
                    EventJson.Options),
                Tools = JsonSerializer.SerializeToElement(
                    tools.All.Select(t => new
                    {
                        name = t.Schema.Name,
                        description = t.Schema.Description,
                        input_schema = t.Schema.InputSchema,
                    }), EventJson.Options),
            };
            lastId = (await events.AppendAsync(conversationId, sp, ct)).Id;
            history.Insert(0, new LlmMessage("system", prompt));
        }

        return (history, lastId);
    }

    private static LlmMessage ToLlmMessage(MessageEvent m)
    {
        try
        {
            var el = m.LlmMessage;
            var role = el.TryGetProperty("role", out var r)
                ? r.GetString() ?? "user" : "user";
            var content = el.TryGetProperty("content", out var c)
                ? c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : c.ValueKind == JsonValueKind.Array
                        ? string.Concat(c.EnumerateArray().Select(b =>
                            b.TryGetProperty("text", out var t)
                                ? t.GetString() : null))
                        : c.GetRawText()
                : null;
            return new LlmMessage(role, content);
        }
        catch (JsonException)
        {
            return new LlmMessage("user", m.LlmMessage.GetRawText());
        }
    }

    private static async Task<string> ObserveAsync(
        IEventService events, Guid conversationId, ActionEvent action,
        ToolResult result, CancellationToken ct)
    {
        var obs = new ObservationEvent
        {
            ParentId = action.Id,
            ActionId = action.Id,
            ToolName = action.ToolName,
            ToolCallId = action.ToolCallId,
            Observation = result.Observation,
        };
        return (await events.AppendAsync(conversationId, obs, ct)).Id;
    }

    private async Task SetStatusAsync(
        IEventService events, AppDbContext db, ConversationRecord conv,
        string status, ConversationExecutionStatus? recordStatus,
        CancellationToken ct, string? pendingAction = null)
    {
        await events.AppendAsync(conversationId, new ConversationStateUpdateEvent
        {
            Key = "execution_status",
            Value = pendingAction is null
                ? JsonSerializer.SerializeToElement(status, EventJson.Options)
                : JsonSerializer.SerializeToElement(
                    new { status, pending_action = pendingAction },
                    EventJson.Options),
        }, CancellationToken.None);
        if (recordStatus is not null)
        {
            conv.ExecutionStatus = recordStatus.Value.ToString();
            conv.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private static JsonElement? TryParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(json);
        }
    }

    private static string? Flatten(JsonElement? el) => el switch
    {
        null => null,
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Array } arr => string.Concat(
            arr.EnumerateArray().Select(b =>
                b.TryGetProperty("text", out var t) ? t.GetString() : null)),
        { } other => other.GetRawText(),
    };
}
