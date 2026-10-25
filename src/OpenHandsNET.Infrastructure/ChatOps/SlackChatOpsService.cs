using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.ChatOps;
using OpenHandsNET.Application.Contracts;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Infrastructure.Scm;
using OpenHandsNET.Infrastructure.Security;

namespace OpenHandsNET.Infrastructure.ChatOps;

/// <summary>
/// Slack inbound pipeline (SPEC-54 RF-001/RF-002/RF-005):
/// verificação v0 HMAC + timestamp ≤5min → idempotência
/// (<c>event_id</c> em webhook_deliveries kind="slack") →
/// thread binding → start-task/send-message. Payloads externos só
/// viram contexto de prompt — nunca executados.
/// </summary>
public sealed class SlackChatOpsService(
    AppDbContext db,
    ISecretStore secrets,
    IChatConnectionService connections,
    OpenHandsNET.Application.Conversations.IAppConversationService conversations,
    OpenHandsNET.Application.Events.IEventService events,
    OpenHandsNET.Application.Agent.IAgentRunnerHost runnerHost,
    IChatSink sink,
    IAuditService audit,
    Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
        scopeFactory,
    OpenHandsNET.Application.Security.IInputGuard inputGuard,
    ILogger<SlackChatOpsService> logger) : IChatOpsService
{
    private const int TimestampToleranceSeconds = 300;

    /// <summary>Verificação <c>X-Slack-Signature</c> v0.</summary>
    internal static bool VerifySignature(
        string signingSecret, string? timestamp, string? signature,
        byte[] payload, long nowUnix)
    {
        if (signature is null || timestamp is null
            || !long.TryParse(timestamp, out var ts)
            || Math.Abs(nowUnix - ts) > TimestampToleranceSeconds
            || !signature.StartsWith("v0=", StringComparison.Ordinal))
        {
            return false;
        }
        var basestring = $"v0:{timestamp}:{Encoding.UTF8.GetString(payload)}";
        var expected = "v0=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret),
                Encoding.UTF8.GetBytes(basestring)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(signature));
    }

    private async Task<bool> VerifyAsync(
        ChatConnection c, string? signature, string? timestamp,
        byte[] payload, CancellationToken ct)
    {
        var secret = await secrets.ResolveAsync(
            "org", c.OrgId?.ToString() ?? "", c.SigningSecretRef, ct);
        return secret is not null
            && VerifySignature(secret, timestamp, signature, payload,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private async Task<bool> IsDuplicateAsync(
        string? eventId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(eventId))
        {
            return false;
        }
        // contexto próprio: chamadas concorrentes (vários eventos no
        // mesmo service) não podem compartilhar `db`.
        await using var scope = scopeFactory.CreateAsyncScope();
        var scoped = scope.ServiceProvider
            .GetRequiredService<AppDbContext>();
        var exists = await scoped.WebhookDeliveries.IgnoreQueryFilters()
            .AnyAsync(d => d.Kind == "slack" && d.DeliveryId == eventId, ct);
        if (exists)
        {
            return true;
        }
        scoped.WebhookDeliveries.Add(new WebhookDeliveryRecord
        {
            Id = Guid.NewGuid(),
            Kind = "slack",
            DeliveryId = eventId,
            ProcessedAt = DateTimeOffset.UtcNow,
        });
        try
        {
            await scoped.SaveChangesAsync(ct);
            return false;
        }
        catch (DbUpdateException)
        {
            return true;
        }
    }

    /// <inheritdoc />
    public async Task<(ChatOpsResult Result, string? Challenge)>
        HandleEventAsync(string? signature, string? timestamp,
            byte[] payload, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(payload); }
        catch (JsonException)
        { return (ChatOpsResult.InvalidSignature, null); }

        using (doc)
        {
            var root = doc.RootElement;
            var teamId = root.TryGetProperty("team_id", out var t)
                ? t.GetString() : null;
            if (teamId is null)
            {
                return (ChatOpsResult.NoConnection, null);
            }
            var connection = await connections.FindByTeamAsync(teamId, ct);
            if (connection is null)
            {
                return (ChatOpsResult.NoConnection, null);
            }
            if (!await VerifyAsync(
                    connection, signature, timestamp, payload, ct))
            {
                return (ChatOpsResult.InvalidSignature, null);
            }

            if (root.TryGetProperty("type", out var ty)
                && ty.GetString() == "url_verification")
            {
                return (ChatOpsResult.Challenge,
                    root.GetProperty("challenge").GetString());
            }
            return await DispatchEventAsync(root, connection, teamId, ct);
        }
    }

    private async Task<(ChatOpsResult, string?)> DispatchEventAsync(
        JsonElement root, ChatConnection connection,
        string teamId, CancellationToken ct)
    {
        var eventId = root.TryGetProperty("event_id", out var eid)
            ? eid.GetString() : null;
        if (await IsDuplicateAsync(eventId, ct))
        {
            return (ChatOpsResult.Duplicate, null);
        }

        if (!root.TryGetProperty("event", out var ev)
            || !ev.TryGetProperty("type", out var et))
        {
            return (ChatOpsResult.Ignored, null);
        }
        var type = et.GetString();

        // Ignorar mensagens de bot (echo próprio).
        if (ev.TryGetProperty("bot_id", out _)
            || (ev.TryGetProperty("subtype", out var st)
                && st.GetString() == "bot_message"))
        {
            return (ChatOpsResult.Ignored, null);
        }

        var channelId = ev.TryGetProperty("channel", out var ch)
            ? ch.GetString() : null;
        var text = ev.TryGetProperty("text", out var tx)
            ? tx.GetString() : null;
        var ts = ev.TryGetProperty("ts", out var tss)
            ? tss.GetString() : null;
        var threadTs = ev.TryGetProperty("thread_ts", out var th)
            ? th.GetString() : ts;
        if (channelId is null || text is null || ts is null)
        {
            return (ChatOpsResult.Ignored, null);
        }

        var config = await connections.GetChannelConfigAsync(
            connection.Id, channelId, ct);
        var isMention = type == "app_mention"
            || (connection.BotUserId is not null
                && text.Contains($"<@{connection.BotUserId}>",
                    StringComparison.Ordinal));
        var isIm = type == "message"
            && ev.TryGetProperty("channel_type", out var cty)
            && cty.GetString() == "im";
        var isTriage = config?.AutoTriage == true
            && type == "message";

        // Thread bound primeiro: replies num thread conhecido vão pra
        // conversa mesmo sem menção/DM/triage no canal.
        var bound = await db.ChatThreadBindings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(b => b.ConnectionId == connection.Id
                && b.ChannelId == channelId
                && b.ThreadTs == threadTs, ct);

        if (bound is not null)
        {
            // Reply no thread → send-message na conversa existente.
            await AppendUserMessageAsync(
                bound.ConversationId,
                UntrustedInput.Wrap(StripMention(text),
                    $"slack:{channelId}", "message.text"), ct);
            return (ChatOpsResult.Processed, null);
        }

        if (!isMention && !isIm && !isTriage)
        {
            return (ChatOpsResult.Ignored, null);
        }

        var ownerUserId = await ResolveOwnerAsync(connection, ct);
        if (ownerUserId is null)
        {
            return (ChatOpsResult.Ignored, null);
        }

        // SPEC-59 RF-002: screening do texto do webhook antes de
        // virar prompt — block → resposta educada + audit (sem o
        // conteúdo, só categorias).
        var verdict = await inputGuard.ScoreAsync(
            text, "webhook:slack", connection.OrgId, ct);
        if (verdict.Verdict
            == Application.Security.InputGuardVerdict.Block)
        {
            await sink.PostMessageAsync(
                connection, channelId, threadTs,
                "Mensagem bloqueada pela política de segurança da "
                    + "org — reformule e tente de novo.",
                ct);
            await audit.RecordAsync(
                "guard.input_blocked", null, connection.OrgId,
                "channel", channelId,
                metadata: new
                {
                    channel = "webhook:slack",
                    categories = verdict.Categories,
                },
                cancellationToken: ct);
            return (ChatOpsResult.Blocked, null);
        }

        var prompt = BuildPrompt(text, isTriage, channelId);
        var task = await conversations.CreateForUserAsync(
            ownerUserId.Value, connection.OrgId,
            new CreateConversationRequest(
                new InitialMessage("user", [prompt], Run: true),
                config?.SelectedRepository,
                AgentType: null, LlmModel: null, SandboxSpecId: null),
            ct);

        var convId = task.AppConversationId ?? Guid.Empty;
        db.ChatThreadBindings.Add(new ChatThreadBindingRecord
        {
            Id = Guid.NewGuid(),
            ConnectionId = connection.Id,
            ChannelId = channelId,
            ThreadTs = threadTs!,
            ConversationId = convId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("chatops.thread_bound",
            actorUserId: ownerUserId.Value.ToString(),
            orgId: connection.OrgId,
            targetType: "app_conversation", targetId: convId.ToString(),
            metadata: new { channelId, threadTs, isTriage },
            cancellationToken: ct);
        await audit.RecordAsync("chatops.run_started",
            orgId: connection.OrgId,
            targetType: "app_conversation", targetId: convId.ToString(),
            metadata: new { teamId, channelId },
            cancellationToken: ct);

        _ = Task.Run(() => PostbackAsync(
                connection, channelId, ts, threadTs!, convId),
            CancellationToken.None);

        return (ChatOpsResult.Processed, null);
    }

    private async Task PostbackAsync(
        ChatConnection connection, string channelId,
        string ts, string threadTs, Guid convId)
    {
        try
        {
            await sink.AddReactionAsync(
                connection, channelId, ts, "eyes",
                CancellationToken.None);
            var statusTs = await sink.PostMessageAsync(
                connection, channelId, threadTs,
                ":arrows_counterclockwise: Working on it — " +
                $"conversation {convId}", CancellationToken.None);
            if (statusTs is not null)
            {
                // Scope novo: o DbContext injetado já foi disposed
                // quando o request webhook retornou.
                using var scope = scopeFactory.CreateScope();
                var db2 = scope.ServiceProvider
                    .GetRequiredService<AppDbContext>();
                var b = await db2.ChatThreadBindings
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.ConversationId == convId
                        && x.ThreadTs == threadTs, CancellationToken.None);
                if (b is not null)
                {
                    b.StatusMessageTs = statusTs;
                    await db2.SaveChangesAsync(CancellationToken.None);
                }
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "chatops postback failed");
        }
    }

    /// <inheritdoc />
    public async Task<(ChatOpsResult Result, string? ReplyText)>
        HandleCommandAsync(string? signature, string? timestamp,
            byte[] rawBody,
            IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        var teamId = form.TryGetValue("team_id", out var t) ? t : null;
        var channelId = form.TryGetValue("channel_id", out var c) ? c : null;
        var text = form.TryGetValue("text", out var x) ? x?.Trim() : null;
        var userId = form.TryGetValue("user_id", out var u) ? u : null;
        if (teamId is null || channelId is null)
        {
            return (ChatOpsResult.NoConnection, null);
        }
        var connection = await connections.FindByTeamAsync(teamId, ct);
        if (connection is null)
        {
            return (ChatOpsResult.NoConnection, null);
        }

        if (!await VerifyAsync(
                connection, signature, timestamp, rawBody, ct))
        {
            return (ChatOpsResult.InvalidSignature, null);
        }

        if (string.IsNullOrEmpty(text))
        {
            return (ChatOpsResult.Processed,
                "Uso: `/openhandsnet <prompt>` ou `/openhandsnet status`.");
        }
        if (text.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            var bindings = await db.ChatThreadBindings.IgnoreQueryFilters()
                .Where(b => b.ConnectionId == connection.Id
                    && b.ChannelId == channelId).ToListAsync(ct);
            return (ChatOpsResult.Processed,
                $"{bindings.Count} thread(s) bound in this channel.");
        }
        if (text.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            return (ChatOpsResult.Processed,
                "Stop ainda não implementado — use a UI (SPEC-54 backlog).");
        }

        var ownerUserId = await ResolveOwnerAsync(connection, ct);
        if (ownerUserId is null)
        {
            return (ChatOpsResult.Processed,
                "Nenhum usuário OpenHandsNET mapeado — configure a org.");
        }
        var config = await connections.GetChannelConfigAsync(
            connection.Id, channelId, ct);
        var task = await conversations.CreateForUserAsync(
            ownerUserId.Value, connection.OrgId,
            new CreateConversationRequest(
                new InitialMessage("user",
                    [$"[Slack /openhandsnet by {userId}] "
                        + UntrustedInput.Wrap(text,
                            $"slack:{channelId}", "command.text")],
                    Run: true),
                config?.SelectedRepository,
                AgentType: null, LlmModel: null, SandboxSpecId: null),
            ct);
        var convId = task.AppConversationId ?? Guid.Empty;
        db.ChatThreadBindings.Add(new ChatThreadBindingRecord
        {
            Id = Guid.NewGuid(),
            ConnectionId = connection.Id,
            ChannelId = channelId,
            ThreadTs = $"cmd:{Guid.NewGuid():N}",
            ConversationId = convId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("chatops.run_started",
            orgId: connection.OrgId,
            targetType: "app_conversation", targetId: convId.ToString(),
            metadata: new { channelId, slash = true, userId },
            cancellationToken: ct);
        return (ChatOpsResult.Processed,
            $":rocket: Sessão criada — conversa `{convId}`.");
    }

    /// <inheritdoc />
    public async Task<ChatOpsResult> HandleInteractionAsync(
        string? signature, string? timestamp, byte[] rawBody,
        byte[] payload, CancellationToken ct)
    {
        // Interactions chegam como form payload={"json"}; signature
        // cobre rawBody, o JSON parseado vem do campo payload.
        JsonDocument doc;
        try { doc = JsonDocument.Parse(payload); }
        catch (JsonException) { return ChatOpsResult.InvalidSignature; }
        using (doc)
        {
            var root = doc.RootElement;
            var teamId = root.TryGetProperty("team", out var tm)
                && tm.TryGetProperty("id", out var ti)
                    ? ti.GetString() : null;
            if (teamId is null)
            {
                return ChatOpsResult.NoConnection;
            }
            var connection = await connections.FindByTeamAsync(teamId, ct);
            if (connection is null)
            {
                return ChatOpsResult.NoConnection;
            }
            if (!await VerifyAsync(
                    connection, signature, timestamp, rawBody, ct))
            {
                return ChatOpsResult.InvalidSignature;
            }

            return await DispatchInteractionAsync(root, connection, ct);
        }
    }

    // confirm/cancel de ask_user → user message no thread bound.
    private async Task<ChatOpsResult> DispatchInteractionAsync(
        JsonElement root, ChatConnection connection, CancellationToken ct)
    {
        var channelId = root.TryGetProperty("channel", out var ch)
            && ch.TryGetProperty("id", out var ci)
                ? ci.GetString() : null;
        var ts = root.TryGetProperty("message", out var msg)
            && msg.TryGetProperty("thread_ts", out var th)
                ? th.GetString() : null;
        var action = root.TryGetProperty("actions", out var acts)
            && acts.GetArrayLength() > 0
            && acts[0].TryGetProperty("value", out var av)
                ? av.GetString() : null;
        if (channelId is null || ts is null || action is null)
        {
            return ChatOpsResult.Ignored;
        }
        var bound = await db.ChatThreadBindings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(b => b.ConnectionId == connection.Id
                && b.ChannelId == channelId && b.ThreadTs == ts, ct);
        if (bound is null)
        {
            return ChatOpsResult.Ignored;
        }
        await AppendUserMessageAsync(bound.ConversationId,
            $"[button:{action}]", ct);
        return ChatOpsResult.Processed;
    }

    private async Task AppendUserMessageAsync(
        Guid conversationId, string text, CancellationToken ct)
    {
        var msg = new MessageEvent
        {
            Source = EventSource.User,
            LlmMessage = JsonSerializer.SerializeToElement(new
            {
                role = "user",
                content = new[] { text },
            }),
        };
        await events.AppendAsync(conversationId, msg, ct);
        await runnerHost.NotifyMessageAsync(conversationId, ct);
    }

    private async Task<Guid?> ResolveOwnerAsync(
        ChatConnection connection, CancellationToken ct)
    {
        if (connection.CreatedByUserId is not null
            && Guid.TryParse(connection.CreatedByUserId, out var createdBy))
        {
            return createdBy;
        }
        if (connection.OrgId is { } orgId)
        {
            var owner = await db.OrgMembers.IgnoreQueryFilters()
                .Where(m => m.OrgId == orgId && m.Role == "owner")
                .Select(m => m.UserId).FirstOrDefaultAsync(ct);
            if (owner != Guid.Empty)
            {
                return owner;
            }
        }
        return null;
    }

    private static string StripMention(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text, "<@[A-Z0-9]+>", "",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromMilliseconds(500)).Trim();

    // SPEC-91: texto do Slack é conteúdo externo — entra envelopado.
    private static string BuildPrompt(
        string text, bool isTriage, string channelId)
    {
        var sb = new StringBuilder();
        sb.Append(isTriage
            ? "[Slack auto-triage: classify bug/question/feature and act or answer]\n"
            : "[Slack trigger]\n");
        sb.Append($"Channel: {channelId}\nInstruction:\n");
        sb.Append(UntrustedInput.Wrap(
            StripMention(text), $"slack:{channelId}", "message.text"));
        return sb.ToString();
    }
}