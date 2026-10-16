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
        // SPEC-26 RF-004: superfície de conexão para o OfflineBanner.
        _conn.Reconnecting += _ => { ConnectionChanged?.Invoke(State); return Task.CompletedTask; };
        _conn.Reconnected += _ => { ConnectionChanged?.Invoke(State); return Task.CompletedTask; };
        _conn.Closed += _ => { ConnectionChanged?.Invoke(State); return Task.CompletedTask; };
    }

    /// <summary>Dispara em Reconnecting/Reconnected/Closed.</summary>
    public event Action<HubConnectionState>? ConnectionChanged;

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

    /// <summary>SPEC-41 RF-005: retoma run pausado.</summary>
    public Task ResumeAsync(Guid conversationId, CancellationToken ct = default) =>
        _conn.InvokeAsync("Resume", conversationId, ct);

    /// <summary>
    /// SPEC-34 RF-001: pseudo-kind streaming_delta — frames efêmeros
    /// <c>{item_id, seq, text, channel}</c>; nunca chegam ao backlog.
    /// SPEC-41 RF-001: <c>channel</c> "reasoning" viaja separado de content.
    /// </summary>
    public async IAsyncEnumerable<DeltaItem> DeltasAsync(
        Guid conversationId,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct = default)
    {
        var channel = await _conn.StreamAsChannelAsync<JsonElement>(
            "Deltas", conversationId, ct);
        while (await channel.WaitToReadAsync(ct))
        {
            while (channel.TryRead(out var f))
            {
                var itemId = f.TryGetProperty("item_id", out var i)
                    ? i.GetString() ?? "" : "";
                var seq = f.TryGetProperty("seq", out var s)
                    ? s.GetInt64() : 0;
                var text = f.TryGetProperty("text", out var t)
                    ? t.GetString() ?? "" : "";
                var ch = f.TryGetProperty("channel", out var c)
                    ? c.GetString() ?? "content" : "content";
                yield return new DeltaItem(itemId, seq, text, ch);
            }
        }
    }

    /// <summary>Frame efêmero do canal de deltas (SPEC-34/41).</summary>
    public sealed record DeltaItem(
        string ItemId, long Seq, string Text, string Channel = "content");

    /// <summary>
    /// SPEC-44 RF-003: stream de ticks de invalidação de attention
    /// (payload-free — refetch no endpoint aplica o scoping).
    /// </summary>
    public async IAsyncEnumerable<DateTimeOffset> AttentionChangesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct = default)
    {
        var channel = await _conn.StreamAsChannelAsync<DateTimeOffset>(
            "AttentionChanges", ct);
        while (await channel.WaitToReadAsync(ct))
        {
            while (channel.TryRead(out var tick))
            {
                yield return tick;
            }
        }
    }

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
