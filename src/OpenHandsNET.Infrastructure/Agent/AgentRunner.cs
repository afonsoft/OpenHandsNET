using System.Security.Cryptography;
using System.Text;
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
    private const string Paused = "paused";
    private const string Error = "error";
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

        // SPEC-24 RF-003: counters de atividade do run (progress/summary).
        var activity = new RunActivityTracker();

        // SPEC-41 RF-004: heartbeat — LLM/tool calls longas não emitem
        // eventos; o ping de 15s mantém o watchdog do client quieto.
        using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Task.Run(
            () => HeartbeatLoopAsync(events, activity, hbCts.Token),
            CancellationToken.None);
        try
        {
            await LoopAsync(events, db, conv, sandbox, executor, tools, mcp,
                llm, condenser, opt, activity, ct);
        }
        catch (OperationCanceledException)
        {
            await HandleInterruptAsync(events, db, conv, sandbox,
                activity);
        }
        finally
        {
            await hbCts.CancelAsync();
            try { await heartbeat; }
            catch (Exception) { /* best-effort — falha tolerada */ }

            // SPEC-07 RF-002: conexões MCP órfãs fecham com a conversa.
            if (mcp is not null)
            {
                await mcp.DisconnectAsync(
                    conversationId, CancellationToken.None);
            }
        }
    }

    private async Task HeartbeatLoopAsync(
        IEventService events, RunActivityTracker activity,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await activity.HeartbeatAsync(events, conversationId,
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { /* cancelamento esperado */ }
        catch (Exception) { /* heartbeat é best-effort */ }
    }

    private async Task HandleInterruptAsync(
        IEventService events, AppDbContext db, ConversationRecord conv,
        Domain.Sandbox.Sandbox sandbox, RunActivityTracker activity)
    {
        // SPEC-24 RF-006: pausa/interrupção também fecha com summary.
        await activity.SummaryAsync(events, conversationId,
            _pauseRequested ? Paused : "interrupted", null,
            CancellationToken.None);
        await SetStatusAsync(events, db, conv,
            _pauseRequested ? Paused : "idle",
            _pauseRequested
                ? ConversationExecutionStatus.Paused
                : ConversationExecutionStatus.Running,
            CancellationToken.None);
        // SPEC-43 RF-002: pausa também fecha o turno com checkpoint
        // (ct já cancelado aqui → token neutro).
        if (_pauseRequested)
        {
            await AutoCheckpointAsync(services, sandbox,
                conversationId, Paused,
                CancellationToken.None);
        }
    }

    private async Task LoopAsync(
        IEventService events, AppDbContext db, ConversationRecord conv,
        Domain.Sandbox.Sandbox sandbox, ISandboxExecutor executor,
        IToolRegistry tools, Application.Mcp.IMcpClientService? mcp,
        IAgentLlmProvider llm, ICondenser condenser,
        AgentLlmOptions opt, RunActivityTracker activity,
        CancellationToken ct)
    {
        var builder = services.GetRequiredService<Context.ConversationHistoryBuilder>();
        var contextMeter = services.GetRequiredService<IContextMeter>();
        // SPEC-29 RF-002: permissões efetivas (org floor → plan overlay →
        // conversa → org → instância); deny remove a tool do catálogo.
        var permissions = await ConversationPermissionResolver.ResolveAsync(
            db, conv, opt, ct);
        var effectiveTools = ConversationPermissionResolver.Catalog(
            tools, permissions);
        // SPEC-48 RF-001/002: política por org — resolvida UMA vez por run
        // (cache TTL 60s); restart do run pega a policy nova.
        var policyService = services.GetService<ISecurityPolicyService>();
        var policy = policyService is not null
            ? await policyService.GetAsync(conv.OrgId, ct)
            : SecurityPolicy.Balanced;
        // SPEC-33 RF-004 + SPEC-48 RF-004: fingerprint do toolset —
        // drift com ResumeStrict/policy strict aborta o run (Error).
        if (!await EmitAgentContractAsync(
                events, db, conv, effectiveTools.All, opt, policy, ct))
        {
            return;
        }

        var rebuilt = await builder.BuildAsync(
            conversationId, conv, effectiveTools, ct);
        var history = rebuilt.Messages;
        var ids = rebuilt.EventIds;
        var lastEventId = rebuilt.LastEventId;
        var confirmationMode = conv.ConfirmationMode;
        var hooks = services.GetService<Application.Hooks.IHookService>();
        // SPEC-33 RF-001: analyzer por ação — High força confirmação
        // (mesmo com permission allow), diagnostic security_risk redacted.
        // SPEC-48 RF-002: AnalyzerOverride da policy troca o analyzer do run
        // (off/pattern/llm com fallback + diagnostic).
        var security = await ResolvePolicyAnalyzerAsync(
            policy, services.GetService<ISecurityAnalyzer>(), events, ct);
        // SPEC-34 RF-001: bus de deltas efêmeros — runner publica,
        // hub repassa; nada disso toca o IEventService.
        var bus = services.GetService<IAgentActivityBus>();
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

        // SPEC-50 RF-003: BYOR — resolvido uma vez por run (mesmo padrão
        // da security policy). byor=strict exige que o modelo esteja na
        // lista permitted do proxy; violação → diagnostic + run Error
        // (fail-closed, nunca cai para o provider local).
        var byor = services.GetService<IByorKeyService>();
        var proxyOpt = services.GetService<IOptions<LlmProxyOptions>>()?.Value;
        ByorCallContext? byorCtx = null;
        if (byor is { Enabled: true } && conv.CreatedByUserId is { } byorUser)
        {
            byorCtx = await byor.ResolveCallContextAsync(byorUser, ct);
            var permitted = await byor.PermittedModelsAsync(ct);
            var rejection = ByorRejection(
                proxyOpt?.Mode ?? "off", model, byorCtx, permitted);
            if (rejection is not null)
            {
                var sep = rejection.IndexOf(':');
                await events.AppendAsync(conversationId,
                    new AgentDiagnosticEvent
                    {
                        Severity = Error,
                        Code = rejection[..sep],
                        Message = rejection[(sep + 2)..],
                    }, CancellationToken.None);
                await SetStatusAsync(events, db, conv, Error,
                    ConversationExecutionStatus.Error, CancellationToken.None);
                return;
            }
        }

        var routeWait = System.Diagnostics.Stopwatch.StartNew();
        var st = new LoopState
        {
            RouteWait = routeWait,
            Services = services,
            Events = events,
            Db = db,
            Conv = conv,
            Sandbox = sandbox,
            Executor = executor,
            Mcp = mcp,
            Llm = llm,
            Condenser = condenser,
            Opt = opt,
            Activity = activity,
            ContextMeter = contextMeter,
            Permissions = permissions,
            EffectiveTools = effectiveTools,
            Policy = policy,
            Security = security,
            Hooks = hooks,
            Bus = bus,
            Router = router,
            ByorCtx = byorCtx,
            History = history,
            Ids = ids,
            LastEventId = lastEventId,
            Model = model,
            Selection = selection,
        };

        for (var iteration = 0; iteration < opt.MaxIterations; iteration++)
        {
            using var iterSpan = Telemetry.OpenNetTelemetry.StartSpan(
                "agent_iteration", conversationId, sandbox.Id.ToString());
            iterSpan?.SetTag("iteration", iteration);
            await activity.ProgressAsync(events, conversationId, iteration,
                opt.MaxIterations, "iteration", ct);
            if (await CheckSignalsAsync(services, events, db, conv))
            {
                return;
            }

            // pending_messages: usuário falou durante o run → entra no histórico.
            st.LastEventId = await DrainPendingAsync(
                hooks, sandbox, st.History, st.Ids, st.LastEventId, ct);

            // SPEC-22: compaction — forced (manual/overflow/stop_length) ou
            // threshold avaliado pelo condenser. Modelo pode promover antes.
            // + ledger periódico (RF-005). Consome os pending flags do turno.
            await MaybeCompactAsync(st, conversationId, ct);

            var request = st.History;
            var toolSchemas = effectiveTools.All.Select(t => t.Schema).ToList();
            // SPEC-07: catálogo MCP dinâmico da conversa (falha → degradado);
            // SPEC-29: mcp__* também respeita o evaluator (glob `mcp__*` deny).
            if (mcp is not null)
            {
                toolSchemas.AddRange(
                    (await mcp.ListToolsAsync(conversationId, ct))
                    .Select(Application.Mcp.McpToolCatalog.ToSchema)
                    .Where(s => permissions.Decide(s.Name)
                        != Application.Agent.PermissionDecision.Deny));
            }

            // SPEC-111: buffers do turno — o stream preenche Text/Reasoning/
            // Calls e o dispatch consome (inclui o item_id efêmero e o
            // batcher de deltas — SPEC-34 RF-001 / SPEC-41 RF-001).
            var itemId = Ulid.NewUlid().ToString();
            var turn = new TurnState
            {
                ItemId = itemId,
                Batcher = st.Bus is null
                    ? null
                    : new Deltas.DeltaBatcher(conversationId, itemId, st.Bus),
            };
            // SPEC-23 RF-003: cada tentativa de modelo emite llm_route —
            // a UI mostra "tentando X (fallback de Y)" em tempo real.
            if (st.Selection is not null)
            {
                await st.Events.AppendAsync(conversationId,
                    new ConversationStateUpdateEvent
                    {
                        Key = "llm_route",
                        Value = JsonSerializer.SerializeToElement(new
                        {
                            model = st.Selection.Model,
                            role = "worker",
                            attempt = st.Selection.Attempt,
                            fallback_from = st.Selection.FallbackFrom,
                        }, EventJson.Options),
                    }, CancellationToken.None);
            }

            var stream = await StreamAttemptAsync(
                st, request, toolSchemas, turn, conversationId, ct);
            if (stream.Verdict == StreamVerdict.Retry)
            {
                iteration--; // retenta a mesma iteração
                continue;
            }
            if (stream.Verdict == StreamVerdict.Abort)
            {
                return;
            }
            turn.ResponseId = stream.ResponseId;
            turn.Iteration = iteration;

            var dverdict = await DispatchToolCallsAsync(
                st, turn, conversationId, ct);
            if (dverdict == DispatchVerdict.Abort)
            {
                return;
            }

        }

        await events.AppendAsync(conversationId, new AgentErrorEvent
        {
            ToolName = "loop",
            ToolCallId = "",
            Error = $"max_iterations ({opt.MaxIterations}) reached",
        }, CancellationToken.None);
        await activity.DiagnosticAsync(events, conversationId, "warn",
            "max_iterations",
            $"max_iterations ({opt.MaxIterations}) reached",
            CancellationToken.None);
        await activity.SummaryAsync(events, conversationId, "stuck", null,
            CancellationToken.None);
        await SetStatusAsync(events, db, conv, "stuck",
            ConversationExecutionStatus.Error, CancellationToken.None);
    }

    /// <summary>
    /// SPEC-111: fase de compaction + ledger do loop. Consome
    /// <see cref="_compactRequested"/>/<see cref="_compactInstructions"/> e
    /// os pending flags de overflow/stop_length do turno corrente.
    /// </summary>
    private async Task MaybeCompactAsync(
        LoopState st, Guid conversationId, CancellationToken ct)
    {
        var forced = ForcedTrigger(st.OverflowPending, st.StopLengthPending);
        var pendingInstructions = _compactInstructions;
        _compactRequested = false;
        _compactInstructions = null;
        st.OverflowPending = false;
        st.StopLengthPending = false;
        var effectiveWindow = st.Selection?.ContextWindow
            ?? st.ContextMeter.ContextWindow(st.Model);
        var start = forced is { } ft
            ? new CompactionStart(ft,
                st.ContextMeter.EstimateTokens(st.History, st.Model),
                effectiveWindow)
            : await st.Condenser.EvaluateAsync(st.History, st.Model, ct);
        if (start is { } s)
        {
            var condensed = await st.Condenser.CondenseAsync(
                st.History, st.Ids, s.Trigger, st.Model,
                cs => Context.CompactionService.EmitRequestAsync(
                    st.Events, conversationId, cs, pendingInstructions, ct),
                ct);
            if (condensed.Compacted)
            {
                var condensation = await Context.CompactionService
                    .EmitCondensationAsync(st.Events, conversationId,
                        condensed, ct);
                // summary message passa a mapear para o evento Condensation.
                if (condensed.SummaryOffset is { } so
                    && so < condensed.ResultEventIds.Count)
                {
                    var resultIds = condensed.ResultEventIds.ToList();
                    resultIds[so] = condensation.Id;
                    condensed = condensed with { ResultEventIds = resultIds };
                }

                st.History = condensed.Messages.ToList();
                st.Ids = condensed.ResultEventIds.ToList();
                st.CompactionCount++;
            }

            st.LastEstimate = condensed.TokensAfter;
        }
        else
        {
            st.LastEstimate = st.ContextMeter.EstimateTokens(
                st.History, st.Model);
        }

        // SPEC-22 RF-005: ledger de contexto (state_update) — periódico
        // e sempre que uma compaction aconteceu.
        if (start is not null
            || ++st.SinceLedger >= st.Opt.Compaction.LedgerEveryIterations)
        {
            st.SinceLedger = 0;
            await Context.CompactionService.EmitLedgerAsync(
                st.Events, conversationId, st.LastEstimate,
                effectiveWindow, st.Model, ct, st.CompactionCount);
        }
    }

    /// <summary>Veredito da tentativa de stream LLM de uma iteração.</summary>
    private enum StreamVerdict
    {
        /// <summary>Stream completou — prosseguir para dispatch de tool calls.</summary>
        Proceed,
        /// <summary>Retentar a iteração (promoção/overflow/fallback).</summary>
        Retry,
        /// <summary>Abortar o run (erro terminal já emitido).</summary>
        Abort,
    }

    private sealed record StreamOutcome(StreamVerdict Verdict, string ResponseId);

    /// <summary>
    /// SPEC-111: fase de stream LLM — consome o provider, acumula
    /// text/reasoning/calls nos buffers do turno e traduz as exceções em
    /// <see cref="StreamVerdict"/> (Retry = promoção, overflow-retry ou
    /// fallback de rota; Abort = erro terminal já emitido/status marcado).
    /// </summary>
    private async Task<StreamOutcome> StreamAttemptAsync(
        LoopState st, List<LlmMessage> request,
        List<ToolSchema> toolSchemas, TurnState turn,
        Guid conversationId, CancellationToken ct)
    {
        var llmTimer = System.Diagnostics.Stopwatch.StartNew();
        var responseId = "";
        try
        {
            using var llmSpan = Telemetry.OpenNetTelemetry.StartSpan(
                "llm_call", conversationId, st.Sandbox.Id.ToString());
            llmSpan?.SetTag("model", st.Model);
            await foreach (var chunk in st.Llm.ChatStreamAsync(
                request, toolSchemas,
                new LlmOptions(Model: st.Model, Byor: st.ByorCtx), ct))
            {
                switch (chunk)
                {
                    case TextDelta d:
                        turn.Text.Append(d.Text);
                        // SPEC-34 RF-001: delta vai só ao bus efêmero —
                        // nunca persistido/replayado pelo IEventService.
                        turn.Batcher?.Add(d.Text);
                        break;
                    case ReasoningDelta rd:
                        turn.Reasoning.Append(rd.Text);
                        // SPEC-41 RF-001: canal separado — o client
                        // renderiza na ThinkingSection, não no content.
                        turn.Batcher?.Add(rd.Text, "reasoning");
                        break;
                    case ToolCallDelta tcd:
                        if (!turn.Calls.TryGetValue(tcd.Index, out var cur))
                        {
                            cur = ("", "", new System.Text.StringBuilder());
                        }

                        cur.id = tcd.Id ?? cur.id;
                        cur.name = tcd.Name ?? cur.name;
                        if (tcd.ArgumentsFragment is { } frag)
                        {
                            cur.args.Append(frag);
                        }

                        turn.Calls[tcd.Index] = cur;
                        break;
                    case CompletionEnd end:
                        responseId = end.ResponseId;
                        // SPEC-22 RF-001: calibração online do meter.
                        st.ContextMeter.ObserveUsage(end.Usage.PromptTokens,
                            st.LastEstimate);
                        if (end.FinishReason == "length"
                            && st.Opt.Compaction.Enabled)
                        {
                            st.StopLengthPending = true;
                        }

                        await st.Events.AppendAsync(conversationId, new TokenEvent
                        {
                            Source = EventSource.Agent,
                            ParentId = st.LastEventId,
                            PromptTokens = end.Usage.PromptTokens,
                            CompletionTokens = end.Usage.CompletionTokens,
                            CostUsd = end.Usage.CostUsd,
                            Model = st.Model,
                            Role = "worker",
                        }, ct);
                        Telemetry.OpenNetTelemetry.LlmTokens
                            .WithLabels("prompt").Inc(end.Usage.PromptTokens);
                        Telemetry.OpenNetTelemetry.LlmTokens
                            .WithLabels("completion").Inc(end.Usage.CompletionTokens);
                        // SPEC-20 RF-004: metering por org/período — o
                        // run atual completa; a quota bloqueia o próximo.
                        if (st.Conv.OrgId is { } meterOrg
                            && st.Services.GetService<Application.Identity.IUsageMeter>()
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

            return new StreamOutcome(StreamVerdict.Proceed, responseId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ContextOverflowException cex)
        {
            // SPEC-22 RF-006: promover para janela maior antes de compactar.
            if (!st.Promoted && st.Opt.PromotionTarget is { } target
                && target != st.Model)
            {
                st.Promoted = true;
                var fromModel = st.Model;
                logger.LogInformation(
                    "Context overflow — promoting model {From} → {To}",
                    fromModel, target);
                await st.Activity.DiagnosticAsync(st.Events, conversationId,
                    "info", "context_promotion",
                    $"context overflow — promoted {fromModel} → {target}",
                    CancellationToken.None);
                st.Model = target;
                st.Conv.LlmModel = target;
                await st.Events.AppendAsync(conversationId,
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
                st.Conv.UpdatedAt = DateTimeOffset.UtcNow;
                await st.Db.SaveChangesAsync(CancellationToken.None);
                return new StreamOutcome(StreamVerdict.Retry, "");
            }

            if (!st.OverflowRetried)
            {
                st.OverflowRetried = true;
                logger.LogWarning(cex,
                    "Context overflow — forcing compaction and retry");
                await st.Activity.DiagnosticAsync(st.Events, conversationId,
                    "warn", "context_overflow",
                    "context overflow — compacting and retrying",
                    CancellationToken.None);
                st.OverflowPending = true;
                return new StreamOutcome(StreamVerdict.Retry, "");
            }

            logger.LogError(cex, "LLM context overflow persists");
            await st.Events.AppendAsync(conversationId, new AgentErrorEvent
            {
                ToolName = "llm",
                ToolCallId = "",
                Error = SecretRedactor.Redact(cex.Message),
            }, CancellationToken.None);
            await st.Activity.DiagnosticAsync(st.Events, conversationId,
                Error, "context_overflow",
                SecretRedactor.Redact(cex.Message), CancellationToken.None);
            await st.Activity.SummaryAsync(st.Events, conversationId,
                Error, "context overflow", CancellationToken.None);
            await SetStatusAsync(st.Events, st.Db, st.Conv, Error,
                ConversationExecutionStatus.Error, CancellationToken.None);
            return new StreamOutcome(StreamVerdict.Abort, "");
        }
        catch (LlmCallException lce) when (st.Router is not null
            && st.Selection is not null)
        {
            // SPEC-23 RF-003: turn recovery — classificação → ação.
            llmTimer.Stop();
            await st.Events.AppendAsync(conversationId,
                new ConversationStateUpdateEvent
                {
                    Key = "llm_route",
                    Value = JsonSerializer.SerializeToElement(new
                    {
                        model = st.Selection.Model,
                        role = "worker",
                        attempt = st.Selection.Attempt,
                        kind = lce.Kind.ToString().ToLowerInvariant(),
                        latency_ms = (int)llmTimer.ElapsedMilliseconds,
                    }, EventJson.Options),
                }, CancellationToken.None);

            if (lce.Kind == LlmErrorKind.Auth)
            {
                logger.LogError("LLM auth failure — aborting run");
                await st.Events.AppendAsync(conversationId, new AgentErrorEvent
                {
                    ToolName = "llm",
                    ToolCallId = "",
                    Error = SecretRedactor.Redact(lce.Message),
                }, CancellationToken.None);
                await SetStatusAsync(st.Events, st.Db, st.Conv, Error,
                    ConversationExecutionStatus.Error,
                    CancellationToken.None);
                return new StreamOutcome(StreamVerdict.Abort, "");
            }

            if (st.RouteWait.Elapsed
                > TimeSpan.FromSeconds(st.Opt.MaxRouteWaitSeconds))
            {
                logger.LogError("Route wait budget exhausted");
                await st.Events.AppendAsync(conversationId, new AgentErrorEvent
                {
                    ToolName = "llm",
                    ToolCallId = "",
                    Error = SecretRedactor.Redact(
                        $"route budget exceeded: {lce.Message}"),
                }, CancellationToken.None);
                await SetStatusAsync(st.Events, st.Db, st.Conv, Error,
                    ConversationExecutionStatus.Error,
                    CancellationToken.None);
                return new StreamOutcome(StreamVerdict.Abort, "");
            }

            var next = await st.Router.NextFallbackAsync(
                st.Selection, lce.Kind, CancellationToken.None);
            if (next is null)
            {
                logger.LogError(lce, "LLM call failed — no fallback left");
                await st.Events.AppendAsync(conversationId, new AgentErrorEvent
                {
                    ToolName = "llm",
                    ToolCallId = "",
                    Error = SecretRedactor.Redact(lce.Message),
                }, CancellationToken.None);
                await SetStatusAsync(st.Events, st.Db, st.Conv, Error,
                    ConversationExecutionStatus.Error,
                    CancellationToken.None);
                return new StreamOutcome(StreamVerdict.Abort, "");
            }

            logger.LogWarning(
                "LLM {Kind} em {From} — fallback para {To}",
                lce.Kind, st.Selection.Model, next.Model);
            st.Model = next.Model;
            st.Selection = next;
            st.Conv.LlmModel = next.Model;
            st.Conv.UpdatedAt = DateTimeOffset.UtcNow;
            await st.Db.SaveChangesAsync(CancellationToken.None);
            return new StreamOutcome(StreamVerdict.Retry, "");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LLM completion failed");
            await st.Events.AppendAsync(conversationId, new AgentErrorEvent
            {
                ToolName = "llm",
                ToolCallId = "",
                Error = SecretRedactor.Redact(ex.Message),
            }, CancellationToken.None);
            await st.Activity.SummaryAsync(st.Events, conversationId,
                Error, SecretRedactor.Redact(ex.Message),
                CancellationToken.None);
            await SetStatusAsync(st.Events, st.Db, st.Conv, Error,
                ConversationExecutionStatus.Error, CancellationToken.None);
            return new StreamOutcome(StreamVerdict.Abort, "");
        }
    }

    /// <summary>
    /// SPEC-111: fase de dispatch — assistant-toolcall → ActionEvents +
    /// execução das tools (permissão, hooks, analyzer, mid-turn overflow,
    /// postbacks, finish). Retorna Abort quando a run termina dentro da
    /// fase (finish/erro), Continue para a próxima iteração.
    /// </summary>
    private async Task<DispatchVerdict> DispatchToolCallsAsync(
        LoopState st, TurnState turn, Guid conversationId,
        CancellationToken ct)
    {

        var toolCalls = turn.Calls.Select(kv => new LlmToolCall(
            kv.Value.id is { Length: > 0 } i ? i : $"call_{turn.ResponseId}_{kv.Key}",
            kv.Value.name, kv.Value.args.ToString())).ToList();

        if (toolCalls.Count == 0)
        {
            if (turn.Text.Length == 0)
            {
                // Completion vazio → nada a fazer; encerra em idle.
                await SetStatusAsync(st.Events, st.Db, st.Conv, "idle",
                    ConversationExecutionStatus.Running, CancellationToken.None);
                return DispatchVerdict.Abort;
            }

            turn.Batcher?.Flush();
            var msg = new MessageEvent
            {
                Source = EventSource.Agent,
                ParentId = st.LastEventId,
                ItemId = turn.ItemId,
                LlmResponseId = turn.ResponseId,
                LlmMessage = JsonSerializer.SerializeToElement(new
                {
                    role = "assistant",
                    content = new[]
                    {
                            new { type = "text", text = turn.Text.ToString() },
                        },
                }, EventJson.Options),
            };
            st.LastEventId = (await st.Events.AppendAsync(
                conversationId, msg, ct)).Id;
            st.History.Add(LlmMessage.Assistant(turn.Text.ToString()));
            st.Ids.Add(msg.Id);
            return DispatchVerdict.Continue;
        }

        // SPEC-22: a mensagem assistant-toolcall cobre N ActionEvents —
        // o eventId composto (join ',') garante forget consistente.
        turn.TcMsgIdx = st.History.Count;
        st.History.Add(LlmMessage.AssistantToolCall(
            turn.Text.Length > 0 ? turn.Text.ToString() : null, toolCalls));
        st.Ids.Add(null);
        var actionIds = new List<string>();
        // SPEC-34: flush antes do primeiro durável — deltas do pensamento
        // saem antes dos ActionEvents que substituem o slot item_id.
        turn.Batcher?.Flush();

        foreach (var call in toolCalls)
        {
            var cverdict = await ExecuteToolCallAsync(
                st, turn, call, actionIds, conversationId, ct);
            if (cverdict == ToolCallVerdict.Abort)
            {
                return DispatchVerdict.Abort;
            }
        }

        // SPEC-22: eventId composto da assistant-msg cobre todas as
        // ActionEvents — forget da mensagem esquece as actions juntas.
        if (turn.TcMsgIdx >= 0 && turn.TcMsgIdx < st.Ids.Count)
        {
            st.Ids[turn.TcMsgIdx] = string.Join(',', actionIds);
        }
        return DispatchVerdict.Continue;
    }
    /// <summary>Veredito da fase de dispatch de tool calls.</summary>
    /// <summary>
    /// SPEC-111: execução de uma tool call — ActionEvent, permissão,
    /// hooks, analyzer, confirmação, execução, observação e postbacks.
    /// Next = seguir para a próxima call; Abort = run terminou.
    /// </summary>
    private async Task<ToolCallVerdict> ExecuteToolCallAsync(
        LoopState st, TurnState turn, LlmToolCall call,
        List<string> actionIds, Guid conversationId, CancellationToken ct)
    {
        var actionEvent = new ActionEvent
        {
            ParentId = st.LastEventId,
            ItemId = turn.ItemId,
            ToolName = call.Name,
            ToolCallId = call.Id,
            LlmResponseId = turn.ResponseId,
            Action = TryParseJson(call.ArgumentsJson),
            Thought = turn.Text.Length > 0
                ? JsonSerializer.SerializeToElement(
                    new[] { new { type = "text", text = turn.Text.ToString() } },
                    EventJson.Options)
                : null,
            // SPEC-41 RF-001: reasoning normalizado do provider —
            // re-renderizável após replay (canal live já expirou).
            ReasoningContent = turn.Reasoning.Length > 0
                ? turn.Reasoning.ToString() : null,
        };
        st.LastEventId = (await st.Events.AppendAsync(
            conversationId, actionEvent, ct)).Id;
        actionIds.Add(actionEvent.Id);

        var tool = st.EffectiveTools.Find(call.Name);
        var decision = st.Permissions.Decide(call.Name);
        if (tool is null)
        {
            // SPEC-29 RF-002: chamada a tool negada é observabilidade
            // (drift do modelo) — diagnostic + erro explícito.
            if (decision == Application.Agent.PermissionDecision.Deny)
            {
                st.LastEventId = (await st.Events.AppendAsync(conversationId,
                    new Domain.Events.AgentDiagnosticEvent
                    {
                        Severity = "warn",
                        Code = "permission_deny",
                        Message = $"tool '{call.Name}' denied by " +
                            "permission policy",
                    }, ct)).Id;
            }

            var obsId = await ObserveAsync(st.Events, conversationId,
                actionEvent,
                ToolResult.Error(decision ==
                    Application.Agent.PermissionDecision.Deny
                        ? $"tool '{call.Name}' denied by permission policy"
                        : $"unknown tool '{call.Name}'"),
                st.Sandbox, st.Executor, ct);
            st.History.Add(LlmMessage.ToolResult(
                call.Id, decision ==
                    Application.Agent.PermissionDecision.Deny
                        ? "{\"error\":\"tool denied by permission policy\"}"
                        : $"{{\"error\":\"unknown tool '{call.Name}'\"}}"));
            st.Ids.Add(obsId);
            st.LastEventId = obsId;
            return ToolCallVerdict.Next;
        }

        // SPEC-11: on_tool_pre — hook blocking pode vetar a ação.
        if (st.Hooks is not null && await st.Hooks.RunAsync(
                Domain.Hooks.HookEvent.OnToolPre,
                new Application.Hooks.HookContext
                {
                    ConversationId = conversationId,
                    Sandbox = st.Sandbox,
                    Services = st.Services,
                    ToolName = call.Name,
                    ActionId = actionEvent.Id,
                    HookInput = actionEvent.Action,
                }, ct))
        {
            var vetoId = await ObserveAsync(st.Events, conversationId,
                actionEvent,
                ToolResult.Error("blocked by hook"),
                st.Sandbox, st.Executor, ct);
            st.History.Add(LlmMessage.ToolResult(
                call.Id, "{\"error\":\"blocked by hook\"}"));
            st.Ids.Add(vetoId);
            st.LastEventId = vetoId;
            return ToolCallVerdict.Next;
        }

        // SPEC-33 RF-001: security analyzer — High força confirmação
        // humana (piso igual ao guardrail destrutivo).
        var risk = st.Security is null || actionEvent.Action is not { } act
            ? SecurityRisk.Low
            : await st.Security.AnalyzeAsync(call.Name, act, ct);
        if (risk >= SecurityRisk.Medium)
        {
            // SPEC-48 RF-002: o diagnostic carrega preset + floor
            // aplicados (observabilidade da decisão por org).
            await st.Events.AppendAsync(conversationId,
                new AgentDiagnosticEvent
                {
                    Severity = risk == SecurityRisk.High
                        ? "warning" : "info",
                    Code = "security_risk",
                    Message = $"tool {call.Name} scored {risk
                        .ToString().ToLowerInvariant()} risk"
                        + $" (policy={st.Policy.Preset}, min_confirm="
                        + st.Policy.MinConfirmRisk.ToString()
                            .ToLowerInvariant() + ")",
                    ParentId = actionEvent.Id,
                }, ct);
        }

        // SPEC-15 RF-003 + SPEC-29 RF-002 + SPEC-33 + SPEC-48:
        // confirmação exigida por regra ask, guardrail destrutivo,
        // analyzer no floor da st.Policy, ask de mcp__* (policy),
        // ou default Risky+confirmation-mode. Allow explícito pula
        // confirmação — exceto destrutiva/floor (pisos).
        var riskAsks = st.Policy.MinConfirmRisk switch
        {
            // low: qualquer tool Risky confirma, mesmo score baixo.
            SecurityRisk.Low => tool.Risky,
            SecurityRisk.Medium => risk >= SecurityRisk.Medium,
            _ => risk == SecurityRisk.High,
        };
        var mcpTool = call.Name.StartsWith("mcp__",
            StringComparison.Ordinal);
        // SPEC-48 RF-002: AskMcpTools explícito (strict) OU o piso do
        // guardrail — o preset `permissive` é o único que relaxa a
        // fronteira de credenciais (opt-out consciente da org).
        var mcpAsk = mcpTool
            && (st.Policy.AskMcpTools || st.Policy.Preset != "permissive");
        var guardrailAsk = AutonomyGuardrail.RequiresHumanApproval(
            call.Name, actionEvent.Action)
            && !(mcpTool && st.Policy.Preset == "permissive"
                && !st.Policy.AskMcpTools);
        var alwaysAsk = (tool.Risky && guardrailAsk) || riskAsks;
        var needsConfirm = alwaysAsk || mcpAsk
            || decision == Application.Agent.PermissionDecision.Ask
            || (decision is null && tool.Risky && st.Conv.ConfirmationMode);
        if (tool.Risky && !needsConfirm)
        {
            st.LastEventId = (await st.Events.AppendAsync(conversationId,
                new DecisionAutoEvent
                {
                    ParentId = st.LastEventId,
                    AskedId = actionEvent.Id,
                    Chosen = "approve",
                    Reason = decision ==
                        Application.Agent.PermissionDecision.Allow
                            ? "permission_allow" : "always_allow",
                }, ct)).Id;
        }

        if (needsConfirm)
        {
            await SetStatusAsync(st.Events, st.Db, st.Conv,
                "waiting_for_confirmation", null, ct,
                pendingAction: actionEvent.Id,
                pendingTool: call.Name);
            var approved = await WaitConfirmationAsync(
                st.Events, st.Db, st.Conv, actionEvent.Id, st.Opt, ct);
            if (approved is null)
            {
                return ToolCallVerdict.Abort; // timeout/interrupt já sinalizados
            }

            if (!approved.Value)
            {
                st.History.Add(LlmMessage.ToolResult(
                    call.Id, "User rejected this action."));
                st.Ids.Add(null); // rejeição sem observation event
                return ToolCallVerdict.Next;
            }

            await SetStatusAsync(st.Events, st.Db, st.Conv, "running",
                ConversationExecutionStatus.Running, ct);
        }

        ToolResult result;
        if (string.Equals(call.Name, "ask_user",
            StringComparison.Ordinal))
        {
            // SPEC-44 RF-001: ask_user — a resposta é a próxima
            // mensagem do usuário no canal _incoming (mesma UX de
            // uma pergunta no chat; kind=question no inbox).
            var answer = await WaitUserAnswerAsync(
                st.Events, st.Db, st.Conv, st.Opt, ct);
            if (answer is null)
            {
                return ToolCallVerdict.Abort; // interrupt/timeout já sinalizados
            }

            result = answer;
        }
        else
        {
            using var toolSpan = Telemetry.OpenNetTelemetry.StartSpan(
                "tool_call", conversationId, st.Sandbox.Id.ToString());
            toolSpan?.SetTag("tool", call.Name);
            Telemetry.OpenNetTelemetry.ToolCalls
                .WithLabels(call.Name).Inc();
            try
            {
                using var toolCts =
                    CancellationTokenSource.CreateLinkedTokenSource(ct);
                toolCts.CancelAfter(
                    TimeSpan.FromSeconds(st.Opt.ToolTimeoutSeconds));
                var ctx = new ToolCallContext(conversationId, st.Sandbox,
                    st.Executor, st.Events, st.Services,
                    TimeSpan.FromSeconds(st.Opt.ToolTimeoutSeconds),
                    call.Id, actionEvent.Id);
                st.Activity.NoteToolCall(call.Name, call.ArgumentsJson);
                result = await tool.Handler(ctx,
                    TryParseJson(call.ArgumentsJson)
                        ?? JsonDocument.Parse("{}").RootElement.Clone(),
                    toolCts.Token);
            }
            catch (OperationCanceledException)
                when (!ct.IsCancellationRequested)
            {
                result = ToolResult.Error("tool timed out");
            }
            catch (Exception ex)
                when (ex is not OperationCanceledException)
            {
                result = ToolResult.Error(
                    SecretRedactor.Redact(ex.Message));
            }
        }

        var observationId = await ObserveAsync(
            st.Events, conversationId, actionEvent, result,
            st.Sandbox, st.Executor, ct);
        st.History.Add(LlmMessage.ToolResult(
            call.Id, result.Observation.GetRawText()));
        st.Ids.Add(observationId);

        // SPEC-22 RF-004: mid-turn — se o resultado estourou o
        // threshold, compacta entre tool calls da mesma iteração.
        if (st.Opt.Compaction.MidTurnEnabled
            && await st.Condenser.EvaluateAsync(
                st.History, st.Model, ct)
                is not null)
        {
            var midResult = await st.Condenser.CondenseAsync(
                st.History, st.Ids, CompactionTrigger.MidTurn, st.Model,
                cs => Context.CompactionService.EmitRequestAsync(
                    st.Events, conversationId, cs, null, ct), ct);
            if (midResult.Compacted)
            {
                var midCond = await Context.CompactionService
                    .EmitCondensationAsync(st.Events, conversationId,
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

                st.History = midResult.Messages.ToList();
                st.Ids = midResult.ResultEventIds.ToList();
                st.CompactionCount++;
                // a msg assistant-toolcall pode ter sido esquecida.
                turn.TcMsgIdx = st.History.FindLastIndex(
                    m => m.ToolCalls is { Count: > 0 });
            }
        }

        // SPEC-11: on_tool_post — auditoria após a observação.
        if (st.Hooks is not null)
        {
            await st.Hooks.RunAsync(Domain.Hooks.HookEvent.OnToolPost,
                new Application.Hooks.HookContext
                {
                    ConversationId = conversationId,
                    Sandbox = st.Sandbox,
                    Services = st.Services,
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
                await st.Events.AppendAsync(conversationId, new MessageEvent
                {
                    Source = EventSource.Agent,
                    ParentId = observationId,
                    LlmResponseId = turn.ResponseId,
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

            await st.Activity.SummaryAsync(st.Events, conversationId,
                "completed", finalText, CancellationToken.None);
            await SetStatusAsync(st.Events, st.Db, st.Conv, "finished",
                ConversationExecutionStatus.Finished, ct);
            // SPEC-52 RF-005: commit status no head SHA do PR
            // (fire-and-forget — nunca falha a run).
            _ = Task.Run(() => st.Services
                    .GetService<Application.Scm.IScmStatusReporter>()?
                    .ReportRunFinishedAsync(
                        conversationId, success: true,
                        CancellationToken.None)
                ?? Task.CompletedTask, CancellationToken.None);
            // SPEC-54 RF-003: postback no thread Slack bound
            // (fire-and-forget — nunca falha a run).
            _ = Task.Run(() => st.Services
                    .GetService<Application.ChatOps.IChatOpsReporter>()?
                    .ReportRunFinishedAsync(
                        conversationId, success: true,
                        finalText, CancellationToken.None)
                ?? Task.CompletedTask, CancellationToken.None);
            // SPEC-55 RF-004: write-back no ticket (comentário +
            // transição) — fire-and-forget.
            _ = Task.Run(() => st.Services
                    .GetService<Application.Tickets.ITicketReporter>()?
                    .ReportRunFinishedAsync(
                        conversationId, success: true,
                        finalText, st.Conv.PullRequestUrl,
                        CancellationToken.None)
                ?? Task.CompletedTask, CancellationToken.None);
            // SPEC-43 RF-002: snapshot automático pós-turno
            // (Checkpoints:Auto) — melhor esforço, nunca falha a run.
            await AutoCheckpointAsync(st.Services, st.Sandbox,
                conversationId, $"turn {turn.Iteration}", ct);
            if (st.Hooks is not null)
            {
                await st.Hooks.RunAsync(Domain.Hooks.HookEvent.OnFinish,
                    new Application.Hooks.HookContext
                    {
                        ConversationId = conversationId,
                        Sandbox = st.Sandbox,
                        Services = st.Services,
                        ToolName = call.Name,
                        ActionId = actionEvent.Id,
                    }, CancellationToken.None);
            }

            return ToolCallVerdict.Abort;
        }

        st.LastEventId = observationId;
        return ToolCallVerdict.Next;
    }

    private enum ToolCallVerdict
    {
        /// <summary>Seguir para a próxima tool call.</summary>
        Next,
        /// <summary>Run terminou dentro da call — propagar Abort.</summary>
        Abort,
    }

    private enum DispatchVerdict
    {
        /// <summary>Prosseguir para a próxima iteração.</summary>
        Continue,
        /// <summary>Run terminou dentro da fase (finish/erro) — sair.</summary>
        Abort,
    }

    /// <summary>
    /// SPEC-111: estado do turno corrente — buffers que o stream preenche
    /// e o dispatch consome (text/reasoning/calls/ids efêmeros).
    /// </summary>
    private sealed class TurnState
    {
        public required string ItemId { get; init; }
        public Deltas.DeltaBatcher? Batcher { get; init; }
        public int Iteration { get; set; }
        public string ResponseId { get; set; } = "";
        /// <summary>Índice da msg assistant-toolcall (id composto — SPEC-22).</summary>
        public int TcMsgIdx { get; set; } = -1;
        public System.Text.StringBuilder Text { get; } = new();
        public System.Text.StringBuilder Reasoning { get; } = new();
        public SortedDictionary<int, (string id, string name,
            System.Text.StringBuilder args)> Calls
        { get; } = new();
    }

    /// <summary>
    /// Loop-carried mutable state of <see cref="LoopAsync"/> — a fase do
    /// loop lê/escreve via <c>st</c> em vez de dezenas de locals capturados
    /// (SPEC-111: extração de fases sem bundles de parâmetros).
    /// </summary>
    private sealed class LoopState
    {
        // Serviços/refs invariantes do run (resolvidos no preâmbulo).
        public required IServiceProvider Services { get; init; }
        public required IEventService Events { get; init; }
        public required AppDbContext Db { get; init; }
        public required ConversationRecord Conv { get; init; }
        public required Domain.Sandbox.Sandbox Sandbox { get; init; }
        public required ISandboxExecutor Executor { get; init; }
        public required Application.Mcp.IMcpClientService? Mcp { get; init; }
        public required IAgentLlmProvider Llm { get; init; }
        public required ICondenser Condenser { get; init; }
        public required AgentLlmOptions Opt { get; init; }
        public required RunActivityTracker Activity { get; init; }
        public required IContextMeter ContextMeter { get; init; }
        public required PermissionEvaluator Permissions { get; init; }
        public required PermissionedToolRegistry EffectiveTools { get; init; }
        public required SecurityPolicy Policy { get; init; }
        public ISecurityAnalyzer? Security { get; init; }
        public Application.Hooks.IHookService? Hooks { get; init; }
        public IAgentActivityBus? Bus { get; init; }
        public IModelRouter? Router { get; init; }
        public ByorCallContext? ByorCtx { get; init; }
        public required System.Diagnostics.Stopwatch RouteWait { get; init; }

        // Estado mutável carregado entre iterações.
        public required List<LlmMessage> History { get; set; }
        public required List<string?> Ids { get; set; }
        public required string? LastEventId { get; set; }
        public required string? Model { get; set; }
        public ModelSelection? Selection { get; set; }
        public bool Promoted { get; set; }
        public bool OverflowRetried { get; set; }
        public bool OverflowPending { get; set; }
        public bool StopLengthPending { get; set; }
        public int CompactionCount { get; set; }
        public int SinceLedger { get; set; }
        public int LastEstimate { get; set; }
        public string? PendingInstructions { get; set; }
    }

    /// <summary>
    /// SPEC-43 RF-002: checkpoint automático pós-turno quando
    /// <c>Checkpoints:Auto</c> (default) e há repo git. Melhor esforço —
    /// falhas vão a <c>agent_diagnostic</c>, nunca derrubam a run.
    /// </summary>
    private async Task AutoCheckpointAsync(IServiceProvider services,
        Domain.Sandbox.Sandbox sandbox, Guid conversationId,
        string label, CancellationToken ct)
    {
        try
        {
            var opt = services.GetService<IOptions<
                Application.Checkpoints.CheckpointOptions>>()?.Value;
            var ckpts = services.GetService<
                Application.Checkpoints.ICheckpointService>();
            if (opt?.Auto is not true || ckpts is null
                || !await ckpts.IsAvailableAsync(sandbox, ct))
            {
                return;
            }

            await ckpts.SnapshotAsync(
                conversationId, sandbox, label, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                var events = services
                    .GetRequiredService<Application.Events.IEventService>();
                await events.AppendAsync(conversationId,
                    new Domain.Events.AgentDiagnosticEvent
                    {
                        Severity = "warn",
                        Code = "checkpoint_failed",
                        Message = $"auto-checkpoint failed: {ex.Message}",
                    }, CancellationToken.None);
            }
            catch { /* diagnóstico é melhor esforço também */ }
        }
    }

    private static string? ByorRejection(
        string mode, string? model, ByorCallContext? byorCtx,
        IReadOnlyList<string> permitted) =>
        (mode, byorCtx, permitted) switch
        {
            ("byor", null, _) =>
                "byor_key_missing: strict BYOR mode requires an "
                    + "active virtual key for the conversation owner",
            ("byor", _, var p) when !p.Contains(model) =>
                $"byor_model_rejected: model '{model}' not in "
                    + $"LiteLLM permitted list (mode={mode})",
            (_, not null, { Count: > 0 } p) when !p.Contains(model) =>
                $"byor_model_rejected: model '{model}' not in "
                    + $"LiteLLM permitted list (mode={mode})",
            _ => null,
        };

    private async Task<string?> DrainPendingAsync(
        Application.Hooks.IHookService? hooks,
        Domain.Sandbox.Sandbox sandbox, List<LlmMessage> history,
        List<string?> ids, string? lastEventId, CancellationToken ct)
    {
        while (_incoming.Reader.TryRead(out var pending))
        {
            history.Add(
                Context.ConversationHistoryBuilder.ToLlmMessage(pending));
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

        return lastEventId;
    }

    private CompactionTrigger? ForcedTrigger(
        bool overflowPending, bool stopLengthPending)
    {
        if (_compactRequested)
        {
            return CompactionTrigger.Manual;
        }

        if (overflowPending)
        {
            return CompactionTrigger.Overflow;
        }

        return stopLengthPending ? CompactionTrigger.StopLength : null;
    }

    /// <summary>Interrupt/pause → status de saída; true = encerrar o loop.</summary>
    private async Task<bool> CheckSignalsAsync(
        IServiceProvider services, IEventService events,
        AppDbContext db, ConversationRecord conv)
    {
        if (!_interruptSeen)
        {
            return false;
        }

        // SPEC-28 RF-004: interrupt/pause propaga p/ subagentes ativos.
        services.GetService<ISubagentHost>()?.AbortAll(conversationId);
        await SetStatusAsync(events, db, conv,
            _pauseRequested ? Paused : "idle",
            _pauseRequested
                ? ConversationExecutionStatus.Paused
                : ConversationExecutionStatus.Running,
            CancellationToken.None);
        return true;
    }

    /// <summary>
    /// SPEC-44 RF-001: <c>ask_user</c> — status
    /// <c>waiting_for_question</c> (sem <c>pending_action</c> — não é
    /// approve/reject; o usuário responde no composer) e kind
    /// <c>question</c> no inbox. A próxima mensagem do usuário em
    /// <see cref="_incoming"/> vira o resultado do tool. null =
    /// interrupt/timeout (status já sinalizado).
    /// </summary>
    private async Task<ToolResult?> WaitUserAnswerAsync(
        IEventService events, AppDbContext db, ConversationRecord conv,
        AgentLlmOptions opt, CancellationToken ct)
    {
        await SetStatusAsync(events, db, conv, "waiting_for_question",
            null, ct,
            attentionKind: Application.Conversations
                .AttentionKinds.Question);
        try
        {
            using var timeout = CancellationTokenSource
                .CreateLinkedTokenSource(ct);
            timeout.CancelAfter(
                TimeSpan.FromMinutes(opt.ConfirmationTimeoutMinutes));
            var msg = await _incoming.Reader.ReadAsync(timeout.Token);
            var text = Context.ConversationHistoryBuilder
                .ToLlmMessage(msg).Content ?? "";
            await SetStatusAsync(events, db, conv, "running",
                ConversationExecutionStatus.Running, ct);
            return ToolResult.Ok(
                string.IsNullOrWhiteSpace(text) ? "(empty answer)" : text);
        }
        catch (OperationCanceledException)
        {
            await events.AppendAsync(conversationId, new InterruptEvent(),
                CancellationToken.None);
            await SetStatusAsync(events, db, conv,
                _pauseRequested ? Paused : "idle",
                _pauseRequested
                    ? ConversationExecutionStatus.Paused
                    : ConversationExecutionStatus.Running,
                CancellationToken.None);
            return null;
        }
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
                _pauseRequested ? Paused : "idle",
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
                // cancelamento esperado ao parar a pump
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
                // exceção agregada esperada no Wait do shutdown
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// SPEC-25 RF-005: aplica o OutputPolicy (cap por tool + truncamento
    /// head+tail + artifact_ref) antes de persistir o observation.
    /// </summary>
    private async Task<string> ObserveAsync(
        IEventService events, Guid conversationId, ActionEvent action,
        ToolResult result, Domain.Sandbox.Sandbox? sandbox,
        ISandboxExecutor? executor, CancellationToken ct)
    {
        var compactor = services.GetService<OpenHandsNET.Application.Common.Interfaces.IOutputCompactorService>();

        JsonElement payload = result.Observation;

        if (compactor != null && payload.ValueKind == JsonValueKind.String)
        {
            var originalText = payload.GetString() ?? string.Empty;
            var compactionResult = await compactor.CompactAsync(originalText, action.ToolName ?? "unknown", ct);

            payload = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                content = compactionResult.CompactContent,
                artifact_ref = compactionResult.ArtifactId
            })).RootElement;
        }
        else
        {
            var policy = services.GetService<Tools.OutputPolicy>();
            payload = policy is null
                ? result.Observation
                : await policy.ApplyAsync(action.ToolName ?? "", action.Id,
                    result.Observation, sandbox, executor, conversationId,
                    services.GetService<
                        Application.Artifacts.IArtifactStore>(), ct);
        }

        var obs = new ObservationEvent
        {
            ParentId = action.Id,
            ActionId = action.Id,
            ToolName = action.ToolName!,
            ToolCallId = action.ToolCallId,
            Observation = payload,
        };
        return (await events.AppendAsync(conversationId, obs, ct)).Id;
    }

    /// <summary>
    /// Atualiza status (evento + record). SPEC-44 RF-001: projeta
    /// attention — <c>waiting_for_confirmation</c> →
    /// <paramref name="attentionKind"/> (default <c>confirmation</c>,
    /// <c>question</c> p/ ask_user), <c>error</c>/<c>stuck</c> →
    /// <c>failed</c>, demais limpam. Roda mesmo quando
    /// <paramref name="recordStatus"/> é null.
    /// </summary>
    private async Task SetStatusAsync(
        IEventService events, AppDbContext db, ConversationRecord conv,
        string status, ConversationExecutionStatus? recordStatus,
        CancellationToken ct, string? pendingAction = null,
        string? pendingTool = null, string? attentionKind = null)
    {
        await events.AppendAsync(conversationId, new ConversationStateUpdateEvent
        {
            Key = "execution_status",
            Value = pendingAction is null
                ? JsonSerializer.SerializeToElement(status, EventJson.Options)
                : JsonSerializer.SerializeToElement(
                    // SPEC-44: client exige pending_action OBJETO
                    // ({action_id, tool}) — string nunca disparava o
                    // ConfirmationBanner (bug latente).
                    new
                    {
                        status,
                        pending_action = new
                        {
                            action_id = pendingAction,
                            tool = pendingTool,
                        },
                    },
                    EventJson.Options),
        }, CancellationToken.None);

        var kind = status switch
        {
            "waiting_for_confirmation" =>
                attentionKind ?? Application.Conversations
                    .AttentionKinds.Confirmation,
            "waiting_for_question" =>
                attentionKind ?? Application.Conversations
                    .AttentionKinds.Question,
            Error or "stuck" =>
                Application.Conversations.AttentionKinds.Failed,
            _ => null,
        };
        var attentionChanged = conv.NeedsAttentionKind != kind;
        var dirty = attentionChanged;
        if (attentionChanged)
        {
            conv.NeedsAttentionKind = kind;
            conv.NeedsAttentionAtTicks = kind is null
                ? null : DateTimeOffset.UtcNow.UtcTicks;
        }

        if (recordStatus is not null)
        {
            conv.ExecutionStatus = recordStatus.Value.ToString();
            dirty = true;
        }

        if (dirty)
        {
            conv.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            if (attentionChanged)
            {
                services.GetService<Application.Conversations
                    .IAttentionBus>()?.PublishChanged();
            }
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

    /// <summary>
    /// SPEC-48 RF-002: resolve o analyzer efetivo do run conforme o
    /// <c>AnalyzerOverride</c> da policy de org — <c>off</c> desliga,
    /// <c>pattern</c> força local, <c>llm</c> exige provider configurado
    /// (fallback para o analyzer global + diagnostic warning quando
    /// indisponível). <c>null</c> mantém o global.
    /// </summary>
    private async Task<ISecurityAnalyzer?> ResolvePolicyAnalyzerAsync(
        SecurityPolicy policy, ISecurityAnalyzer? global,
        IEventService events, CancellationToken ct)
    {
        switch (policy.AnalyzerOverride)
        {
            case null:
                return global;
            case "off":
                return null;
            case "pattern":
                return new Security.PatternSecurityAnalyzer();
            case "llm":
                {
                    var llmProvider = services.GetService<IAgentLlmProvider>();
                    var mem = services.GetService<
                        Microsoft.Extensions.Caching.Memory.IMemoryCache>();
                    if (llmProvider is not null && mem is not null)
                    {
                        return new Security.LlmSecurityAnalyzer(llmProvider, mem);
                    }

                    await events.AppendAsync(conversationId,
                        new AgentDiagnosticEvent
                        {
                            Severity = "warning",
                            Code = "security_analyzer_fallback",
                            Message = "org policy requests llm analyzer but no "
                                + "provider is configured — falling back to "
                                + "the global analyzer",
                        }, ct);
                    return global;
                }
            default:
                return global;
        }
    }

    /// <summary>
    /// SPEC-33 RF-004: grava o <c>agent_contract</c> (agent_type + fingerprint
    /// do toolset efetivo) e compara com o contrato anterior do stream —
    /// tools removidas entre runs emitem <c>resume_drift</c>. SPEC-48 RF-004:
    /// fail-closed quando <c>AgentLlm:StrictResume</c> ou a policy de org
    /// pede strict — o run termina com diagnostic <c>error</c> + status
    /// <c>Error</c> (retorna <c>false</c> para abortar o loop).
    /// </summary>
    private async Task<bool> EmitAgentContractAsync(
        IEventService events, AppDbContext db, ConversationRecord conv,
        IReadOnlyList<AgentTool> effectiveTools, AgentLlmOptions opt,
        SecurityPolicy? policy, CancellationToken ct)
    {
        var names = effectiveTools.Select(t => t.Schema.Name)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        var fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                string.Join(',', names) + "|" + conv.AgentType)))[..16];

        // Contrato anterior = último state update agent_contract no stream.
        var prior = await events.SearchAsync(conversationId, new EventQuery(
            KindEq: "ConversationStateUpdateEvent", Limit: 100), ct);
        var removed = new List<string>();
        string? priorAgent = null;
        foreach (var e in prior.Items.Reverse())
        {
            if (e is not ConversationStateUpdateEvent
                {
                    Key: "agent_contract",
                    Value: { } v
                })
            {
                continue;
            }

            priorAgent = v.TryGetProperty("agent_type", out var at)
                ? at.GetString() : null;
            var prevTools = v.TryGetProperty("tools", out var pt)
                ? pt.EnumerateArray().Select(t => t.GetString() ?? "")
                    .ToHashSet(StringComparer.Ordinal) : [];
            removed = prevTools.Except(names).ToList();
            break;
        }

        if (removed.Count > 0 || (priorAgent is not null
            && priorAgent != conv.AgentType))
        {
            var msg = $"resume drift: removed tools [{string.Join(
                ", ", removed)}]"
                + (priorAgent != conv.AgentType
                    ? $", agent_type {priorAgent} -> {conv.AgentType}" : "");
            // SPEC-48 RF-004: strict = policy de org (override) → global.
            var strict = policy?.ResumeStrict ?? opt.IsStrictResume;
            if (strict)
            {
                // Fail-closed nunca é silencioso: diagnostic error +
                // status Error antes de abortar o run.
                await events.AppendAsync(conversationId,
                    new AgentDiagnosticEvent
                    {
                        Severity = Error,
                        Code = "resume_drift",
                        Message = msg,
                    }, CancellationToken.None);
                await SetStatusAsync(events, db, conv, Error,
                    ConversationExecutionStatus.Error, CancellationToken.None);
                return false;
            }

            await events.AppendAsync(conversationId,
                new AgentDiagnosticEvent
                {
                    Severity = "warning",
                    Code = "resume_drift",
                    Message = msg,
                }, ct);
        }

        await events.AppendAsync(conversationId,
            new ConversationStateUpdateEvent
            {
                Key = "agent_contract",
                Value = JsonSerializer.SerializeToElement(new
                {
                    agent_type = conv.AgentType,
                    tools = names,
                    fingerprint,
                }, EventJson.Options),
            }, ct);
        return true;
    }
}