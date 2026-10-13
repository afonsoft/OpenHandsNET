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
    IEventService events, TerminalBridgeService terminals) : Hub
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
    /// Confirmation mode: registra a decisão do usuário como evento (SPEC-09
    /// consome no agent loop).
    /// </summary>
    public async Task ConfirmAction(
        Guid conversationId, string actionId, bool approved, string? reason,
        CancellationToken ct)
    {
        EventBase e = approved
            ? new MessageEvent
            {
                Source = EventSource.User,
                LlmMessage = System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    role = "user",
                    content = new[] { new
                    {
                        type = "text",
                        text = $"[confirmed] action {actionId}",
                    } },
                }, EventJson.Options),
            }
            : new UserRejectObservation
            {
                Source = EventSource.Environment,
                ToolName = "",
                ToolCallId = "",
                RejectionReason = reason ?? "rejected by user",
                ActionId = actionId,
            };
        await events.AppendAsync(conversationId, e, ct);
    }

    /// <summary>stdin do terminal interativo.</summary>
    public async Task TerminalIn(Guid conversationId, string data, CancellationToken ct) =>
        await terminals.WriteAsync(conversationId, data, ct);

    /// <summary>stdout/stderr do terminal (stream para o caller).</summary>
    public IAsyncEnumerable<string> TerminalOut(
        Guid conversationId, CancellationToken ct) =>
        terminals.SubscribeAsync(conversationId, ct);
}
