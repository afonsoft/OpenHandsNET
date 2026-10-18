using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Api.Hubs;

/// <summary>
/// SignalR edge hub for conversations (SPEC-04 RF-005).
/// <see cref="JoinConversation"/> streams backlog + live events;
/// terminal I/O bridges to the sandbox bash session.
/// </summary>
public sealed class ConversationHub(
    IEventService events, TerminalBridgeService terminals,
    IAgentActivityBus activity,
    OpenHandsNET.Application.Conversations.IAttentionBus attention,
    OpenHandsNET.Application.Agent.IAgentRunnerHost runnerHost) : Hub
{
    /// <summary>
    /// Entra no grupo da conversa e faz stream: backlog a partir de
    /// <paramref name="lastSeenEventId"/> + eventos live (ordem garantida).
    /// </summary>
    public async IAsyncEnumerable<EventBase> JoinConversation(
        Guid conversationId, string? lastSeenEventId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Groups.AddToGroupAsync(
            Context.ConnectionId, conversationId.ToString(), ct);

        // Backlog: eventos após lastSeenEventId (cursor de reconexão).
        if (lastSeenEventId is not null)
        {
            var seen = (await events.GetBatchAsync(
                    conversationId, [lastSeenEventId], ct))
                .FirstOrDefault();
            var after = seen?.Timestamp;
            string? cursor = null;
            do
            {
                var page = await events.SearchAsync(conversationId, new EventQuery(
                    TimestampGte: after, PageId: cursor, Limit: 100), ct);
                foreach (var e in page.Items)
                {
                    if (e.Timestamp == after && e.Id == lastSeenEventId)
                    {
                        continue;
                    }

                    yield return e;
                }

                cursor = page.NextPageId;
            }
            while (cursor is not null);
        }

        // Live.
        await foreach (var e in events.SubscribeAsync(conversationId, ct))
        {
            yield return e;
        }
    }

    /// <summary>
    /// SPEC-34 RF-001: pseudo-kind <c>streaming_delta</c> — tokens efêmeros
    /// do run corrente. Nunca persistidos/replayados: o evento durável com
    /// o mesmo <c>item_id</c> substitui o slot provisório no client.
    /// </summary>
    public IAsyncEnumerable<DeltaFrame> Deltas(
        Guid conversationId, CancellationToken ct) =>
        activity.SubscribeAsync(conversationId, ct);

    /// <summary>
    /// SPEC-44 RF-003: stream de ticks de invalidação do inbox de
    /// attention (payload-free — o client refaz o fetch com scoping).
    /// </summary>
    public IAsyncEnumerable<DateTimeOffset> AttentionChanges(
        CancellationToken ct) =>
        attention.SubscribeAsync(ct);

    /// <summary>Envia mensagem do usuário (aparece no stream como MessageEvent).</summary>
    public async Task SendUserMessage(
        Guid conversationId, string content, CancellationToken ct)
    {
        var message = new MessageEvent
        {
            Source = EventSource.User,
            LlmMessage = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                role = "user",
                content = new[] { new { type = "text", text = content } },
            }, EventJson.Options),
        };
        await events.AppendAsync(conversationId, message, ct);
    }

    /// <summary>
    /// Confirmation mode: registra a decisão do usuário e destrava o runner
    /// que aguarda a ação pendente (SPEC-09 RF-004).
    /// </summary>
    public async Task ConfirmAction(
        Guid conversationId, string actionId, bool approved, string? reason,
        CancellationToken ct) =>
        await runnerHost.ConfirmActionAsync(
            conversationId, actionId, approved, reason, ct);

    /// <summary>Interrompe o run corrente com graça (SPEC-09).</summary>
    public async Task Interrupt(Guid conversationId, CancellationToken ct) =>
        await runnerHost.InterruptAsync(conversationId, ct);

    /// <summary>Pausa o run corrente (resumível) — SPEC-09.</summary>
    public async Task Pause(Guid conversationId, CancellationToken ct) =>
        await runnerHost.PauseAsync(conversationId, ct);

    /// <summary>Retoma um run pausado — SPEC-41 RF-005.</summary>
    public async Task Resume(Guid conversationId, CancellationToken ct) =>
        await runnerHost.ResumeAsync(conversationId, ct);

    /// <summary>
    /// SPEC-53 RF-002: Live Takeover - Operador assume o controle manual do terminal/browser.
    /// Pausa o agente para evitar conflitos de input/output no sandbox.
    /// </summary>
    public async Task AcquireControl(Guid conversationId, string surface, CancellationToken ct)
    {
        await runnerHost.PauseAsync(conversationId, ct);
    }

    /// <summary>
    /// SPEC-53 RF-002: Live Takeover - Operador devolve o controle ao agente com sumário da intervenção.
    /// Injeta HumanIntervenedObservation no stream e retoma a execução.
    /// </summary>
    public async Task ReleaseControl(
        Guid conversationId, string surface, string summary, IReadOnlyList<string>? commands, CancellationToken ct)
    {
        var obs = new HumanIntervenedObservation
        {
            Surface = surface ?? "terminal",
            Summary = summary,
            CommandsExecuted = commands
        };
        await events.AppendAsync(conversationId, obs, ct);
        await runnerHost.ResumeAsync(conversationId, ct);
    }

    /// <summary>stdin do terminal interativo.</summary>
    public async Task TerminalIn(Guid conversationId, string data, CancellationToken ct) =>
        await terminals.WriteAsync(conversationId, data, ct);

    /// <summary>stdout/stderr do terminal (stream para o caller).</summary>
    public IAsyncEnumerable<string> TerminalOut(
        Guid conversationId, CancellationToken ct) =>
        terminals.SubscribeAsync(conversationId, ct);
}
