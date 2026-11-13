using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.ChatOps;
using Sandcastle.Application.Contracts;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.ChatOps;

/// <summary>
/// Slack Web API <see cref="IChatSink"/> (SPEC-54 RF-003): bot token
/// por-referência, Bearer auth, nunca loga token. Falhas viram warn +
/// null — postback nunca derruba o pipeline.
/// SPEC-147 RF-001: todo texto outbound passa por
/// <see cref="SlackTextSanitizer"/> neste único ponto de saída —
/// pings de grupo só escapam quando a org opta in
/// (<c>chatops_allow_group_mentions</c>) ou a instância habilita
/// <c>ChatOps:AllowGroupMentions</c>.
/// </summary>
public sealed class SlackChatSink(
    IHttpClientFactory httpFactory, ISecretStore secrets,
    AppDbContext db, IOptions<ChatOpsOptions> options,
    ILogger<SlackChatSink> logger) : IChatSink
{
    private const string ApiBase = "https://slack.com/api/";

    /// <summary>
    /// Org setting (<c>organizations.settings_json</c>) que permite
    /// pings de grupo explicitamente (SPEC-147 RF-001). Alias legado —
    /// a chave canônica vive em <see cref="GroupMentionGate"/>.
    /// </summary>
    public const string OrgSettingAllowGroupMentions =
        GroupMentionGate.OrgSettingAllowGroupMentions;

    private async Task<string> SanitizeAsync(
        ChatConnection c, string text, CancellationToken ct)
    {
        if (await GroupMentionGate.AllowsAsync(
                db, options.Value, c.OrgId, ct))
        {
            return text;
        }

        return ChatTextSanitizer.NeutralizeSlackPings(text);
    }

    private async Task<HttpRequestMessage> ReqAsync(
        ChatConnection c, string method, CancellationToken ct)
    {
        var token = c.BotTokenSecretRef is null ? null
            : await secrets.ResolveAsync(
                "org", c.OrgId?.ToString() ?? "", c.BotTokenSecretRef,
                ct);
        if (token is null)
        {
            throw new InvalidOperationException(
                "bot token secret missing");
        }
        var req = new HttpRequestMessage(HttpMethod.Post, ApiBase + method);
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", token);
        req.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    private async Task<JsonElement?> CallAsync(
        ChatConnection c, string method, object body, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient("chatops-slack");
            using var req = await ReqAsync(c, method, ct);
            req.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8,
                "application/json");
            var resp = await http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            var root = JsonDocument.Parse(text).RootElement;
            if (!root.TryGetProperty("ok", out var ok)
                || !ok.GetBoolean())
            {
                logger.LogWarning(
                    "slack {Method} not ok: {Body}", method,
                    text[..Math.Min(200, text.Length)]);
                return null;
            }
            return root.Clone();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "slack {Method} failed", method);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> PostMessageAsync(
        ChatConnection connection, string channelId, string? threadTs,
        string text, CancellationToken ct)
    {
        var root = await CallAsync(connection, "chat.postMessage", new
        {
            channel = channelId,
            thread_ts = threadTs,
            text = await SanitizeAsync(connection, text, ct),
        }, ct);
        return root?.TryGetProperty("ts", out var ts) == true
            ? ts.GetString() : null;
    }

    /// <inheritdoc />
    public async Task UpdateMessageAsync(
        ChatConnection connection, string channelId, string ts,
        string text, CancellationToken ct) =>
        await CallAsync(connection, "chat.update", new
        {
            channel = channelId,
            ts,
            text = await SanitizeAsync(connection, text, ct),
        }, ct);

    /// <inheritdoc />
    public async Task AddReactionAsync(
        ChatConnection connection, string channelId, string ts,
        string emoji, CancellationToken ct) =>
        await CallAsync(connection, "reactions.add", new
        {
            channel = channelId,
            timestamp = ts,
            name = emoji,
        }, ct);

    /// <inheritdoc />
    public async Task SetStatusAsync(
        ChatConnection connection, string channelId, string threadTs,
        string status, CancellationToken ct) =>
        await CallAsync(connection, "assistant.threads.setStatus", new
        {
            channel_id = channelId,
            thread_ts = threadTs,
            status,
        }, ct);
}