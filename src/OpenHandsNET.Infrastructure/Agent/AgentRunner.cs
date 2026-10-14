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
    private volatile bool _compactRequested;
    private volatile string? _compactInstructions;

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
            // SPEC-22 RF-004: compaction manual via API chega como request
            // do usuário; requests emitidos pelo próprio runner são
            // Source=Environment e não re-disparam.
            case CondensationRequest { Source: EventSource.User } r:
                _compactRequested = true;
                _compactInstructions = r.Instructions;
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
        var mcp = services.GetService<Application.Mcp.IMcpClientService>();
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
            await LoopAsync(events, db, conv, sandbox, executor, tools, mcp,
                llm, condenser, opt, ct);
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
        finally
        {
            // SPEC-07 RF-002: conexões MCP órfãs fecham com a conversa.
            if (mcp is not null)
            {
                await mcp.DisconnectAsync(
                    conversationId, CancellationToken.None);
            }
        }
    }

    private async Task LoopAsync(
        IEventService events, AppDbContext db, ConversationRecord conv,
        Domain.Sandbox.Sandbox sandbox, ISandboxExecutor executor,
        IToolRegistry tools, Application.Mcp.IMcpClientService? mcp,
        IAgentLlmProvider llm, ICondenser condenser,
        AgentLlmOptions opt, CancellationToken ct)
    {
        var builder = services.GetRequiredService<Context.ConversationHistoryBuilder>();
        var contextMeter = services.GetRequiredService<IContextMeter>();
        var rebuilt = await builder.BuildAsync(conversationId, conv, tools, ct);
        var history = rebuilt.Messages;
        var ids = rebuilt.EventIds;
        var lastEventId = rebuilt.LastEventId;
        var confirmationMode = conv.ConfirmationMode;
        var hooks = services.GetService<Application.Hooks.IHookService>();
        var model = conv.LlmModel ?? opt.Model;
        // SPEC-23 RF-002: seleção por role + fallback chain. `selection`
        // acompanha o modelo efetivo (catalogo → janela/provider).
        var router = services.GetService<IModelRouter>();
        var selection = router is not null
            ? await router.SelectAsync(conversationId, ModelRole.Worker,
                conv.LlmModel, ct)
            : null;
        if (selection is not null)
        {
            model = selection.Model;
        }

        var routeWait = System.Diagnostics.Stopwatch.StartNew();
        var overflowRetried = false;
        var promoted = false;
        var overflowPending = false;
        var stopLengthPending = false;
        var compactionCount = 0;
        var sinceLedger = 0;
        var lastEstimate = 0;

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
                history.Add(Context.ConversationHistoryBuilder.ToLlmMessage(pending));
                ids.Add(pending.Id);
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

            // SPEC-22: compaction — forced (manual/overflow/stop_length) ou
            // threshold avaliado pelo condenser. Modelo pode promover antes.
            CompactionTrigger? forced = _compactRequested
                ? CompactionTrigger.Manual
                : overflowPending ? CompactionTrigger.Overflow
                : stopLengthPending ? CompactionTrigger.StopLength : null;
            var pendingInstructions = _compactInstructions;
            _compactRequested = false;
            _compactInstructions = null;
            overflowPending = false;
            stopLengthPending = false;
            var effectiveWindow = selection?.ContextWindow
                ?? contextMeter.ContextWindow(model);
            var start = forced is { } ft
                ? new CompactionStart(ft,
                    contextMeter.EstimateTokens(history, model),
                    effectiveWindow)
                : await condenser.EvaluateAsync(history, model, ct);
            if (start is { } s)
            {
                var condensed = await condenser.CondenseAsync(
                    history, ids, s.Trigger, model,
                    cs => Context.CompactionService.EmitRequestAsync(
                        events, conversationId, cs, pendingInstructions),
                    ct);
                if (condensed.Compacted)
                {
                    var condensation = await Context.CompactionService
                        .EmitCondensationAsync(events, conversationId,
                            condensed, ct);
                    // summary message passa a mapear para o evento Condensation.
                    if (condensed.SummaryOffset is { } so
                        && so < condensed.ResultEventIds.Count)
                    {
                        var resultIds = condensed.ResultEventIds.ToList();
                        resultIds[so] = condensation.Id;
                        condensed = condensed with { ResultEventIds = resultIds };
                    }

                    history = condensed.Messages.ToList();
                    ids = condensed.ResultEventIds.ToList();
                    compactionCount++;
                }

                lastEstimate = condensed.TokensAfter;
            }
            else
            {
                lastEstimate = contextMeter.EstimateTokens(history, model);
            }

            // SPEC-22 RF-005: ledger de contexto (state_update) — periódico
            // e sempre que uma compaction aconteceu.
            if (start is not null
                || ++sinceLedger >= opt.Compaction.LedgerEveryIterations)
            {
                sinceLedger = 0;
                await Context.CompactionService.EmitLedgerAsync(
                    events, conversationId, lastEstimate,
                    effectiveWindow, model, ct, compactionCount);
            }

            var request = history;
            var toolSchemas = tools.All.Select(t => t.Schema).ToList();
            // SPEC-07: catálogo MCP dinâmico da conversa (falha → degradado).
            if (mcp is not null)
            {
                toolSchemas.AddRange(
                    (await mcp.ListToolsAsync(conversationId, ct))
                    .Select(Application.Mcp.McpToolCatalog.ToSchema));
            }

            var text = new System.Text.StringBuilder();
            var calls = new SortedDictionary<int, (string id, string name,
                System.Text.StringBuilder args)>();
            string? finishReason = null;
            var responseId = "";
            // SPEC-23 RF-003: cada tentativa de modelo emite llm_route —
            // a UI mostra "tentando X (fallback de Y)" em tempo real.
            if (selection is not null)
            {
                await events.AppendAsync(conversationId,
                    new ConversationStateUpdateEvent
                    {
                        Key = "llm_route",
                        Value = JsonSerializer.SerializeToElement(new
                        {
                            model = selection.Model,
                            role = "worker",
                            attempt = selection.Attempt,
                            fallback_from = selection.FallbackFrom,
                        }, EventJson.Options),
                    }, CancellationToken.None);
            }

            var llmTimer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var llmSpan = Telemetry.OpenNetTelemetry.StartSpan(
                    "llm_call", conversationId, sandbox.Id.ToString());
                llmSpan?.SetTag("model", model);
                await foreach (var chunk in llm.ChatStreamAsync(
                    request, toolSchemas, new LlmOptions(Model: model), ct))
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
                            // SPEC-22 RF-001: calibração online do meter.
                            contextMeter.ObserveUsage(end.Usage.PromptTokens,
                                lastEstimate);
                            if (end.FinishReason == "length"
                                && opt.Compaction.Enabled)
                            {
                                stopLengthPending = true;
                            }

                            await events.AppendAsync(conversationId, new TokenEvent
                            {
                                Source = EventSource.Agent,
                                ParentId = lastEventId,
                                PromptTokens = end.Usage.PromptTokens,
                                CompletionTokens = end.Usage.CompletionTokens,
                                CostUsd = end.Usage.CostUsd,
                                Model = model,
                                Role = "worker",
                            }, ct);
                            Telemetry.OpenNetTelemetry.LlmTokens
                                .WithLabels("prompt").Inc(end.Usage.PromptTokens);
                            Telemetry.OpenNetTelemetry.LlmTokens
                                .WithLabels("completion").Inc(end.Usage.CompletionTokens);
                            // SPEC-20 RF-004: metering por org/período — o
                            // run atual completa; a quota bloqueia o próximo.
                            if (conv.OrgId is { } meterOrg
                                && services.GetService<Application.Identity.IUsageMeter>()
                                    is { } meter)
                            {
                                await meter.RecordTokensAsync(meterOrg,
                                    end.Usage.PromptTokens
                                        + end.Usage.CompletionTokens,
                                    end.Usage.CostUsd, ct);
                            }

                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ContextOverflowException cex)
            {
                // SPEC-22 RF-006: promover para janela maior antes de compactar.
                if (!promoted && opt.PromotionTarget is { } target
                    && target != model)
                {
                    promoted = true;
                    var fromModel = model;
                    logger.LogInformation(
                        "Context overflow — promoting model {From} → {To}",
                        fromModel, target);
                    model = target;
                    conv.LlmModel = target;
                    await events.AppendAsync(conversationId,
                        new ConversationStateUpdateEvent
                        {
                            Key = "model_change",
                            Value = JsonSerializer.SerializeToElement(new
                            {
                                from = fromModel,
                                to = target,
                                reason = "context_promotion",
                            }, EventJson.Options),
                        }, CancellationToken.None);
                    conv.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(CancellationToken.None);
                    iteration--; // retenta a mesma iteração com janela maior
                    continue;
                }

                if (!overflowRetried)
                {
                    overflowRetried = true;
                    logger.LogWarning(cex,
                        "Context overflow — forcing compaction and retry");
                    overflowPending = true;
                    iteration--;
                    continue;
                }

                logger.LogError(cex, "LLM context overflow persists");
                await events.AppendAsync(conversationId, new AgentErrorEvent
                {
                    ToolName = "llm", ToolCallId = "",
                    Error = SecretRedactor.Redact(cex.Message),
                }, CancellationToken.None);
                await SetStatusAsync(events, db, conv, "error",
                    ConversationExecutionStatus.Error, CancellationToken.None);
                return;
            }
            catch (LlmCallException lce) when (router is not null
                && selection is not null)
            {
                // SPEC-23 RF-003: turn recovery — classificação → ação.
                llmTimer.Stop();
                await events.AppendAsync(conversationId,
                    new ConversationStateUpdateEvent
                    {
                        Key = "llm_route",
                        Value = JsonSerializer.SerializeToElement(new
                        {
                            model = selection.Model,
                            role = "worker",
                            attempt = selection.Attempt,
                            kind = lce.Kind.ToString().ToLowerInvariant(),
                            latency_ms = (int)llmTimer.ElapsedMilliseconds,
                        }, EventJson.Options),
                    }, CancellationToken.None);

                if (lce.Kind == LlmErrorKind.Auth)
                {
                    logger.LogError("LLM auth failure — aborting run");
                    await events.AppendAsync(conversationId, new AgentErrorEvent
                    {
                        ToolName = "llm", ToolCallId = "",
                        Error = SecretRedactor.Redact(lce.Message),
                    }, CancellationToken.None);
                    await SetStatusAsync(events, db, conv, "error",
                        ConversationExecutionStatus.Error,
                        CancellationToken.None);
                    return;
                }

                if (routeWait.Elapsed
                    > TimeSpan.FromSeconds(opt.MaxRouteWaitSeconds))
                {
                    logger.LogError("Route wait budget exhausted");
                    await events.AppendAsync(conversationId, new AgentErrorEvent
                    {
                        ToolName = "llm", ToolCallId = "",
                        Error = SecretRedactor.Redact(
                            $"route budget exceeded: {lce.Message}"),
                    }, CancellationToken.None);
                    await SetStatusAsync(events, db, conv, "error",
                        ConversationExecutionStatus.Error,
                        CancellationToken.None);
                    return;
                }

                var next = await router.NextFallbackAsync(
                    selection, lce.Kind, CancellationToken.None);
                if (next is null)
                {
                    logger.LogError(lce, "LLM call failed — no fallback left");
                    await events.AppendAsync(conversationId, new AgentErrorEvent
                    {
                        ToolName = "llm", ToolCallId = "",
                        Error = SecretRedactor.Redact(lce.Message),
                    }, CancellationToken.None);
                    await SetStatusAsync(events, db, conv, "error",
                        ConversationExecutionStatus.Error,
                        CancellationToken.None);
                    return;
                }

                logger.LogWarning(
                    "LLM {Kind} em {From} — fallback para {To}",
                    lce.Kind, selection.Model, next.Model);
                model = next.Model;
                selection = next;
                conv.LlmModel = next.Model;
                conv.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                iteration--; // retenta a iteração com o modelo seguinte
                continue;
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
                ids.Add(msg.Id);
                continue;
            }

            // SPEC-22: a mensagem assistant-toolcall cobre N ActionEvents —
            // o eventId composto (join ',') garante forget consistente.
            var tcMsgIdx = history.Count;
            history.Add(LlmMessage.AssistantToolCall(
                text.Length > 0 ? text.ToString() : null, toolCalls));
            ids.Add(null);
            var actionIds = new List<string>();

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
                actionIds.Add(actionEvent.Id);

                var tool = tools.Find(call.Name);
                if (tool is null)
                {
                    var obsId = await ObserveAsync(events, conversationId,
                        actionEvent,
                        ToolResult.Error($"unknown tool '{call.Name}'"), ct);
                    history.Add(LlmMessage.ToolResult(
                        call.Id, $"{{\"error\":\"unknown tool '{call.Name}'\"}}"));
                    ids.Add(obsId);
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
                    ids.Add(vetoId);
                    lastEventId = vetoId;
                    continue;
                }

                // SPEC-15 RF-003: always-allow auto-aprova ação rotineira
                // (auditável via decision_auto); destrutiva sempre pergunta.
                var alwaysAsk = tool.Risky &&
                    AutonomyGuardrail.RequiresHumanApproval(
                        call.Name, actionEvent.Action);
                if (tool.Risky && !confirmationMode && !alwaysAsk)
                {
                    lastEventId = (await events.AppendAsync(conversationId,
                        new DecisionAutoEvent
                        {
                            ParentId = lastEventId,
                            AskedId = actionEvent.Id,
                            Chosen = "approve",
                            Reason = "always_allow",
                        }, ct)).Id;
                }

                if (tool.Risky && (confirmationMode || alwaysAsk))
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
                        ids.Add(null); // rejeição sem observation event
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
                ids.Add(observationId);

                // SPEC-22 RF-004: mid-turn — se o resultado estourou o
                // threshold, compacta entre tool calls da mesma iteração.
                if (opt.Compaction.MidTurnEnabled
                    && await condenser.EvaluateAsync(history, model, ct)
                        is { } mid)
                {
                    var midResult = await condenser.CondenseAsync(
                        history, ids, CompactionTrigger.MidTurn, model,
                        cs => Context.CompactionService.EmitRequestAsync(
                            events, conversationId, cs, null), ct);
                    if (midResult.Compacted)
                    {
                        var midCond = await Context.CompactionService
                            .EmitCondensationAsync(events, conversationId,
                                midResult, ct);
                        if (midResult.SummaryOffset is { } so2
                            && so2 < midResult.ResultEventIds.Count)
                        {
                            var midIds = midResult.ResultEventIds.ToList();
                            midIds[so2] = midCond.Id;
                            midResult = midResult with
                            {
                                ResultEventIds = midIds,
                            };
                        }

                        history = midResult.Messages.ToList();
                        ids = midResult.ResultEventIds.ToList();
                        compactionCount++;
                        // a msg assistant-toolcall pode ter sido esquecida.
                        tcMsgIdx = history.FindLastIndex(
                            m => m.ToolCalls is { Count: > 0 });
                    }
                }

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

            // SPEC-22: eventId composto da assistant-msg cobre todas as
            // ActionEvents — forget da mensagem esquece as actions juntas.
            if (tcMsgIdx >= 0 && tcMsgIdx < ids.Count)
            {
                ids[tcMsgIdx] = string.Join(',', actionIds);
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

}
