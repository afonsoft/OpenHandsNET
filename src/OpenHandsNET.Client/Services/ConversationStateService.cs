using System.Text.Json;

namespace OpenHandsNET.Client.Services;

/// <summary>
/// Fonte única de verdade da página de conversa (invariant SPEC-05):
/// acumula eventos do hub/backlog, deriva estado de execução, ação
/// pendente, task list e uso de tokens. Componentes assinam
/// <see cref="Changed"/> e nunca escrevem estado diretamente.
/// </summary>
public sealed class ConversationStateService : IAsyncDisposable
{
    private readonly OpenHandsNetApiClient _api;
    private readonly ConversationHubClient _hub;
    private readonly ToastService? _toasts;
    private readonly List<ClientEvent> _events = [];
    private CancellationTokenSource? _cts;

    public ConversationStateService(
        OpenHandsNetApiClient api, ConversationHubClient hub,
        ToastService? toasts = null)
    {
        _api = api;
        _hub = hub;
        _toasts = toasts;
    }

    /// <summary>Disparado após qualquer mutação de estado relevante.</summary>
    public event Action? Changed;

    /// <summary>Eventos da conversa, em ordem de chegada.</summary>
    public IReadOnlyList<ClientEvent> Events => _events;

    /// <summary>
    /// SPEC-34: deltas efêmeros por <c>item_id</c> — slot provisório que o
    /// evento durável com o mesmo item_id substitui (removido em
    /// <see cref="Apply"/>). Ordem de chegada preservada.
    /// </summary>
    public IReadOnlyDictionary<string, string> LiveDeltas => _liveDeltas;
    private readonly Dictionary<string, System.Text.StringBuilder>
        _deltas = new();
    private readonly Dictionary<string, string> _liveDeltas = new();

    /// <summary>
    /// SPEC-41 RF-001: reasoning efêmero por item_id (canal "reasoning") —
    /// mesma semântica de supersedes que <see cref="LiveDeltas"/>, mas
    /// renderizado na ThinkingSection, não no content.
    /// </summary>
    public IReadOnlyDictionary<string, string> LiveReasoning => _liveReasoning;
    private readonly Dictionary<string, System.Text.StringBuilder>
        _reasoning = new();
    private readonly Dictionary<string, string> _liveReasoning = new();

    public ConversationItem? Conversation { get; private set; }
    public string ExecutionStatus { get; private set; } = "IDLE";
    public string? ErrorMessage { get; private set; }
    public PendingActionInfo? PendingAction { get; private set; }
    public IReadOnlyList<PlanItem> Plan { get; private set; } = [];
    public long PromptTokens { get; private set; }
    public long CompletionTokens { get; private set; }
    public double CostUsd { get; private set; }
    public bool Connected { get; private set; }
    public bool LoadingBacklog { get; private set; } = true;

    /// <summary>SPEC-41 RF-004: instante do último evento/delta recebido.</summary>
    public DateTimeOffset LastEventAt { get; private set; } =
        DateTimeOffset.UtcNow;

    /// <summary>
    /// SPEC-41 RF-004: true quando o run está RUNNING e há &gt;45s sem
    /// evento/delta/heartbeat — sinal de stall para a UI.
    /// </summary>
    public bool Stalled { get; private set; }

    /// <summary>Buffer de saída do terminal (live + obs de bash).</summary>
    public System.Text.StringBuilder TerminalBuffer { get; } = new();

    /// <summary>Screenshots de browser (filmstrip — último no fim).</summary>
    public List<string> Screenshots { get; } = [];

    /// <summary>Ação pendente de confirmação (confirmation mode).</summary>
    public sealed record PendingActionInfo(string ActionId, string Summary);

    /// <summary>Item do planner derivado de task_tracker actions.</summary>
    public sealed record PlanItem(string Title, string Status);

    /// <summary>Carrega conversa + backlog e abre o stream live do hub.</summary>
    public async Task OpenAsync(string conversationId, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Conversation = await _api.GetConversationAsync(conversationId, _cts.Token);
        ExecutionStatus = Conversation?.ExecutionStatus ?? "IDLE";

        var backlog = await _api.GetEventsAsync(conversationId, limit: 100, ct: _cts.Token);
        foreach (var e in backlog)
        {
            Apply(e);
        }

        LoadingBacklog = false;
        Changed?.Invoke();

        if (_hub.State != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
        {
            await _hub.StartAsync(_cts.Token);
        }

        Connected = true;
        LastEventAt = DateTimeOffset.UtcNow;
        Changed?.Invoke();

        // SPEC-41 RF-004: watchdog de stall — avalia o silêncio a cada 5s;
        // heartbeats de run_progress renovam LastEventAt e mantêm Stalled=false.
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    var stalled = ExecutionStatus == "RUNNING"
                        && DateTimeOffset.UtcNow - LastEventAt
                            > TimeSpan.FromSeconds(45);
                    if (stalled != Stalled)
                    {
                        Stalled = stalled;
                        Changed?.Invoke();
                    }
                }
            }
            catch (OperationCanceledException) { /* dispose */ }
        }, _cts.Token);

        var lastId = _events.Count > 0 ? _events[^1].Id : null;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in _hub.JoinAsync(
                    Guid.Parse(conversationId), lastId, _cts.Token))
                {
                    Apply(e);
                    Changed?.Invoke();
                }
            }
            catch (OperationCanceledException) { /* dispose */ }
            catch (Exception ex)
            {
                ErrorMessage = $"stream error: {ex.Message}";
                Connected = false;
                Changed?.Invoke();
            }
        }, _cts.Token);

        // SPEC-34: pump do canal de deltas — efêmero, paralelo ao stream
        // durável; falha dele nunca derruba o stream principal.
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var d in _hub.DeltasAsync(
                    Guid.Parse(conversationId), _cts.Token))
                {
                    ApplyDelta(d);
                    Changed?.Invoke();
                }
            }
            catch (OperationCanceledException) { /* dispose */ }
            catch (Exception) { /* deltas são best-effort */ }
        }, _cts.Token);
    }

    /// <summary>Aplica um frame de delta ao slot efêmero do item_id.</summary>
    internal void ApplyDelta(ConversationHubClient.DeltaItem d)
    {
        LastEventAt = DateTimeOffset.UtcNow;
        // SPEC-41 RF-001: canal "reasoning" acumula em slot paralelo.
        var store = d.Channel == "reasoning" ? _reasoning : _deltas;
        var live = d.Channel == "reasoning" ? _liveReasoning : _liveDeltas;
        if (!store.TryGetValue(d.ItemId, out var buf))
        {
            buf = new System.Text.StringBuilder();
            store[d.ItemId] = buf;
            live[d.ItemId] = "";
        }

        buf.Append(d.Text);
        live[d.ItemId] = buf.ToString();
    }

    /// <summary>Envia mensagem de usuário via REST (aparece no stream).</summary>
    public Task SendMessageAsync(string text, CancellationToken ct = default) =>
        _api.SendMessageAsync(Conversation!.Id, text, run: true, ct);

    public Task ConfirmAsync(bool approved, string? reason = null,
        CancellationToken ct = default) =>
        PendingAction is null
            ? Task.CompletedTask
            : _hub.ConfirmActionAsync(Guid.Parse(Conversation!.Id),
                PendingAction.ActionId, approved, reason, ct);

    public Task InterruptAsync(CancellationToken ct = default) =>
        _hub.InterruptAsync(Guid.Parse(Conversation!.Id), ct);

    /// <summary>SPEC-41 RF-005: pausa (resumível) o run corrente.</summary>
    public Task PauseAsync(CancellationToken ct = default) =>
        _hub.PauseAsync(Guid.Parse(Conversation!.Id), ct);

    /// <summary>SPEC-41 RF-005: retoma run pausado.</summary>
    public Task ResumeAsync(CancellationToken ct = default) =>
        _hub.ResumeAsync(Guid.Parse(Conversation!.Id), ct);

    /// <summary>Aplica um evento ao estado derivado (único writer).</summary>
    internal void Apply(ClientEvent e)
    {
        if (_events.Any(x => x.Id == e.Id))
        {
            return; // dedup backlog/live overlap
        }

        LastEventAt = DateTimeOffset.UtcNow;
        // SPEC-34: durável supersedes — evento com item_id descarta o
        // slot de deltas efêmeros correspondente.
        if (e.ItemId is { } itemId)
        {
            _deltas.Remove(itemId);
            _liveDeltas.Remove(itemId);
            _reasoning.Remove(itemId);
            _liveReasoning.Remove(itemId);
        }

        _events.Add(e);
        switch (e.Kind)
        {
            case "ConversationStateUpdateEvent":
                var v = e.Get("value");
                if (v.ValueKind == JsonValueKind.String)
                {
                    ExecutionStatus = v.GetString()!
                        .ToUpperInvariant();
                }
                else if (v.ValueKind == JsonValueKind.Object)
                {
                    // upstream: { execution_status, pending_action, ... }
                    if (v.TryGetProperty("execution_status", out var es))
                    {
                        ExecutionStatus = es.GetString()!.ToUpperInvariant();
                    }

                    if (v.TryGetProperty("pending_action", out var pa)
                        && pa.ValueKind == JsonValueKind.Object)
                    {
                        PendingAction = new PendingActionInfo(
                            pa.TryGetProperty("action_id", out var aid)
                                ? aid.GetString() ?? "" : "",
                            pa.TryGetProperty("tool", out var t)
                                ? t.ToString() : pa.ToString());
                    }

                    if (v.TryGetProperty("pending_action", out var pa2)
                        && pa2.ValueKind is JsonValueKind.Null)
                    {
                        PendingAction = null;
                    }
                }
                break;

            case "TokenEvent":
                if (e.Get("prompt_tokens").ValueKind == JsonValueKind.Number)
                {
                    PromptTokens += e.Get("prompt_tokens").GetInt64();
                }

                if (e.Get("completion_tokens").ValueKind == JsonValueKind.Number)
                {
                    CompletionTokens += e.Get("completion_tokens").GetInt64();
                }

                if (e.Get("cost_usd").ValueKind == JsonValueKind.Number)
                {
                    CostUsd += e.Get("cost_usd").GetDouble();
                }
                break;

            case "ObservationEvent":
                var obs = e.Get("observation");
                if (obs.ValueKind == JsonValueKind.String)
                {
                    TerminalBuffer.AppendLine(obs.GetString());
                }
                else if (obs.ValueKind == JsonValueKind.Object
                    && obs.TryGetProperty("screenshot_base64", out var shot))
                {
                    Screenshots.Add(shot.GetString() ?? "");
                }
                else if (obs.ValueKind == JsonValueKind.Object
                    && obs.TryGetProperty("output", out var o)
                    && o.ValueKind == JsonValueKind.String)
                {
                    // bash/file obs: { exit_code, output, error, timed_out }
                    TerminalBuffer.AppendLine(o.GetString());
                }
                else if (obs.ValueKind == JsonValueKind.Object)
                {
                    TerminalBuffer.AppendLine(obs.ToString());
                }
                break;

            case "UserRejectObservation":
                PendingAction = null;
                break;

            case "AgentErrorEvent":
            case "ServerErrorEvent":
                ErrorMessage = e.Str("error") ?? e.Str("message")
                    ?? e.Raw.ToString()[..Math.Min(200, e.Raw.ToString().Length)];
                // SPEC-26 RF-004: erros de run viram toast persistente.
                _toasts?.Error(ErrorMessage);
                break;

            case "agent_diagnostic":
                // SPEC-26 RF-004: diagnósticos ≥warn notificam.
                var sev = e.Str("severity");
                if (sev is "warn" or "warning" or "error")
                {
                    var msg = $"{e.Str("code")}: {e.Str("message")}";
                    if (sev is "error") { _toasts?.Error(msg); }
                    else { _toasts?.Warn(msg); }
                }
                break;

            case "ActionEvent":
                UpdatePlan(e);
                if (e.ToolCommand() is { } cmd)
                {
                    TerminalBuffer.AppendLine($"$ {cmd}");
                }
                break;
        }
    }

    private void UpdatePlan(ClientEvent e)
    {
        if (e.ToolName() is not ("task_tracker" or "plan"))
        {
            return;
        }

        // task_tracker.arguments: { tasks: [{title,status}, ...] } ou
        // { command: "plan", task_list: [...] } — tolerante a formatos.
        var tool = e.Get("tool");
        if (!tool.TryGetProperty("arguments", out var args)
            || args.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var items = new List<PlanItem>();
        foreach (var item in args.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.Array)
            .SelectMany(p => p.Value.EnumerateArray()))
        {
            var title = TitleOf(item);
            var status = item.TryGetProperty("status", out var s)
                ? s.GetString() ?? "pending" : "pending";
            if (title.Length > 0)
            {
                items.Add(new PlanItem(title, status));
            }
        }

        if (items.Count > 0)
        {
            Plan = items;
        }
    }

    private static string TitleOf(JsonElement item)
    {
        if (item.TryGetProperty("title", out var t))
        {
            return t.GetString() ?? "";
        }

        if (item.TryGetProperty("task", out var t2))
        {
            return t2.GetString() ?? "";
        }

        return item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? "" : "";
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }
    }
}