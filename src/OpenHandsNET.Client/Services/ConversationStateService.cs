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
    private readonly List<ClientEvent> _events = [];
    private CancellationTokenSource? _cts;

    public ConversationStateService(
        OpenHandsNetApiClient api, ConversationHubClient hub)
    {
        _api = api;
        _hub = hub;
    }

    /// <summary>Disparado após qualquer mutação de estado relevante.</summary>
    public event Action? Changed;

    /// <summary>Eventos da conversa, em ordem de chegada.</summary>
    public IReadOnlyList<ClientEvent> Events => _events;

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
        Changed?.Invoke();

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

    /// <summary>Aplica um evento ao estado derivado (único writer).</summary>
    internal void Apply(ClientEvent e)
    {
        if (_events.Any(x => x.Id == e.Id))
        {
            return; // dedup backlog/live overlap
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

                    PendingAction =
                        v.TryGetProperty("pending_action", out var pa)
                        && pa.ValueKind == JsonValueKind.Object
                            ? new PendingActionInfo(
                                pa.TryGetProperty("action_id", out var aid)
                                    ? aid.GetString() ?? "" : "",
                                pa.TryGetProperty("tool", out var t)
                                    ? t.ToString() : pa.ToString())
                            : PendingAction;
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
        foreach (var prop in args.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in prop.Value.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t)
                    ? t.GetString() ?? ""
                    : item.TryGetProperty("task", out var t2)
                        ? t2.GetString() ?? ""
                        : item.ValueKind == JsonValueKind.String
                            ? item.GetString() ?? "" : "";
                var status = item.TryGetProperty("status", out var s)
                    ? s.GetString() ?? "pending" : "pending";
                if (title.Length > 0)
                {
                    items.Add(new PlanItem(title, status));
                }
            }
        }

        if (items.Count > 0)
        {
            Plan = items;
        }
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
