using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Application.ChatOps;

namespace Sandcastle.Infrastructure.ChatOps;

/// <summary>
/// SPEC-151 RF-003 — despachante <see cref="IChatSink"/> por
/// <see cref="ChatConnection.Kind"/>: Slack →
/// <see cref="SlackChatSink"/>, Teams → <see cref="TeamsChatSink"/>.
/// Consumers existentes continuam resolvendo <c>IChatSink</c> único.
/// </summary>
public sealed class ChatSinkRouter(IServiceProvider sp) : IChatSink
{
    private IChatSink For(ChatConnection connection) =>
        connection.Kind == ChatKind.Teams
            ? sp.GetRequiredService<TeamsChatSink>()
            : sp.GetRequiredService<SlackChatSink>();

    /// <inheritdoc />
    public Task<string?> PostMessageAsync(
        ChatConnection connection, string channelId, string? threadTs,
        string text, CancellationToken ct) =>
        For(connection).PostMessageAsync(
            connection, channelId, threadTs, text, ct);

    /// <inheritdoc />
    public Task UpdateMessageAsync(
        ChatConnection connection, string channelId, string ts,
        string text, CancellationToken ct) =>
        For(connection).UpdateMessageAsync(
            connection, channelId, ts, text, ct);

    /// <inheritdoc />
    public Task AddReactionAsync(
        ChatConnection connection, string channelId, string ts,
        string emoji, CancellationToken ct) =>
        For(connection).AddReactionAsync(
            connection, channelId, ts, emoji, ct);

    /// <inheritdoc />
    public Task SetStatusAsync(
        ChatConnection connection, string channelId, string threadTs,
        string status, CancellationToken ct) =>
        For(connection).SetStatusAsync(
            connection, channelId, threadTs, status, ct);
}