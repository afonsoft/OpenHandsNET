using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.ChatOps;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.ChatOps;

/// <summary>
/// Postback de fim de run no thread Slack bound (SPEC-54 RF-003):
/// ✅ reaction + update da mensagem de status + resposta final.
/// Fire-and-forget — falhas só são logadas.
/// </summary>
public sealed class ChatOpsReporter(
    AppDbContext db,
    IChatSink sink,
    IChatConnectionService connections,
    ILogger<ChatOpsReporter> logger) : IChatOpsReporter
{
    /// <inheritdoc />
    public async Task ReportRunFinishedAsync(
        Guid conversationId, bool success, string? summary,
        CancellationToken ct)
    {
        try
        {
            var bound = await db.ChatThreadBindings.IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    b => b.ConversationId == conversationId, ct);
            if (bound is null)
            {
                return;
            }
            var connection = await connections.GetAsync(
                bound.ConnectionId, ct);
            if (connection is null)
            {
                return;
            }

            var safe = summary is null
                ? null
                : OpenHandsNET.Infrastructure.Agent.SecretRedactor
                    .Redact(summary);
            var text = success
                ? $":white_check_mark: Done — {safe ?? "run finished"}"
                : ":x: Run failed — check the conversation for details.";
            await sink.PostMessageAsync(
                connection, bound.ChannelId, bound.ThreadTs, text, ct);
            await sink.AddReactionAsync(
                connection, bound.ChannelId, bound.ThreadTs,
                success ? "white_check_mark" : "x", ct);
            if (bound.StatusMessageTs is not null)
            {
                await sink.UpdateMessageAsync(
                    connection, bound.ChannelId, bound.StatusMessageTs,
                    success
                        ? ":white_check_mark: Finished."
                        : ":x: Failed.", ct);
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "chatops report failed (conv {Id})", conversationId);
        }
    }
}