using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace OpenHandsNET.Client.Services;

/// <summary>
/// SignalR client do <c>/hubs/conversations</c>. Expõe o stream de eventos
/// (backlog + live) como <see cref="IAsyncEnumerable{T}"/> e os comandos
/// interativos (mensagem, confirmação, interrupt, terminal I/O).
/// </summary>
public sealed class ConversationHubClient : IAsyncDisposable
{
    private readonly HubConnection _conn;

    public ConversationHubClient(Uri hubUri)
    {
        _conn = new HubConnectionBuilder()
            .WithUrl(hubUri)
            .WithAutomaticReconnect()
            .AddJsonProtocol(o => o.PayloadSerializerOptions
                .PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
            .Build();
    }

    public HubConnectionState State => _conn.State;

    public Task StartAsync(CancellationToken ct = default) =>
        _conn.StartAsync(ct);

    /// <summary>Backlog (a partir de lastSeenEventId) + stream live.</summary>
    public async IAsyncEnumerable<ClientEvent> JoinAsync(
        Guid conversationId, string? lastSeenEventId,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct = default)
    {
        var stream = await _conn.StreamAsChannelAsync<JsonElement>(
            "JoinConversation", conversationId, lastSeenEventId, ct);
        while (await stream.WaitToReadAsync(ct))
        {
            while (stream.TryRead(out var item))
            {
                yield return new ClientEvent { Raw = item };
            }
        }
    }

    public Task SendUserMessageAsync(
        Guid conversationId, string content, CancellationToken ct = default) =>
        _conn.InvokeAsync("SendUserMessage", conversationId, content, ct);

    public Task ConfirmActionAsync(
        Guid conversationId, string actionId, bool approved, string? reason,
        CancellationToken ct = default) =>
        _conn.InvokeAsync(
            "ConfirmAction", conversationId, actionId, approved, reason, ct);

    public Task InterruptAsync(Guid conversationId, CancellationToken ct = default) =>
        _conn.InvokeAsync("Interrupt", conversationId, ct);

    public Task PauseAsync(Guid conversationId, CancellationToken ct = default) =>
        _conn.InvokeAsync("Pause", conversationId, ct);

    /// <summary>stdout/stderr live do terminal do sandbox.</summary>
    public async IAsyncEnumerable<string> TerminalOutAsync(
        Guid conversationId,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct = default)
    {
        var channel = await _conn.StreamAsChannelAsync<string>(
            "TerminalOut", conversationId, ct);
        while (await channel.WaitToReadAsync(ct))
        {
            while (channel.TryRead(out var chunk))
            {
                yield return chunk;
            }
        }
    }

    public Task TerminalInAsync(
        Guid conversationId, string data, CancellationToken ct = default) =>
        _conn.InvokeAsync("TerminalIn", conversationId, data, ct);

    public async ValueTask DisposeAsync() => await _conn.DisposeAsync();
}
