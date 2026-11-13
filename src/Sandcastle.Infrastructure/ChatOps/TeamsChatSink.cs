using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.ChatOps;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.ChatOps;

/// <summary>
/// Microsoft Teams <see cref="IChatSink"/> via Incoming Webhook
/// (SPEC-151 RF-003 — v1 = webhook, zero-setup; Graph API fase 2).
/// Posta MessageCard JSON na <c>webhook_url</c> da connection.
/// Webhooks não suportam update/reaction/status — esses métodos são
/// no-op documentado. Falhas viram warn + null — nunca derrubam o
/// pipeline. Texto outbound passa por
/// <see cref="ChatTextSanitizer.NeutralizeTeamsPings"/> salvo opt-in.
/// </summary>
public sealed class TeamsChatSink(
    IHttpClientFactory httpFactory,
    AppDbContext db, IOptions<ChatOpsOptions> options,
    ILogger<TeamsChatSink> logger) : IChatSink
{
    private async Task<string> SanitizeAsync(
        ChatConnection c, string text, CancellationToken ct)
    {
        if (await GroupMentionGate.AllowsAsync(
                db, options.Value, c.OrgId, ct))
        {
            return text;
        }

        return ChatTextSanitizer.NeutralizeTeamsPings(text);
    }

    /// <summary>
    /// MessageCard (Office 365 connector card) — formato aceito pelo
    /// Incoming Webhook clássico do Teams.
    /// </summary>
    internal static object BuildCard(string text, string? title = null) =>
        new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType =
                        "application/vnd.microsoft.card.adaptive",
                    contentUrl = (string?)null,
                    content = new
                    {
                        type = "AdaptiveCard",
                        version = "1.4",
                        body = new object[]
                        {
                            new
                            {
                                type = "TextBlock",
                                text = title ?? "Sandcastle",
                                weight = "bolder",
                                size = "medium",
                            },
                            new
                            {
                                type = "TextBlock",
                                text,
                                wrap = true,
                            },
                        },
                    },
                },
            },
        };

    /// <inheritdoc />
    public async Task<string?> PostMessageAsync(
        ChatConnection connection, string channelId, string? threadTs,
        string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(connection.WebhookUrl))
        {
            logger.LogWarning(
                "teams connection {Id} without webhook_url", connection.Id);
            return null;
        }
        if (!Uri.TryCreate(connection.WebhookUrl, UriKind.Absolute,
                out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            logger.LogWarning(
                "teams connection {Id} with invalid webhook_url",
                connection.Id);
            return null;
        }

        try
        {
            var http = httpFactory.CreateClient("chatops-teams");
            var safe = await SanitizeAsync(connection, text, ct);
            var body = JsonSerializer.Serialize(BuildCard(safe));
            using var resp = await http.PostAsync(uri,
                new StringContent(body, Encoding.UTF8, "application/json"),
                ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "teams webhook {Status}", (int)resp.StatusCode);
                return null;
            }
            // Webhook não devolve message id — ts sintético p/ logging.
            return $"teams:{Guid.NewGuid():N}";
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "teams post failed");
            return null;
        }
    }

    /// <inheritdoc />
    public Task UpdateMessageAsync(
        ChatConnection connection, string channelId, string ts,
        string text, CancellationToken ct) =>
        // Incoming webhook não edita mensagens — no-op documentado.
        Task.CompletedTask;

    /// <inheritdoc />
    public Task AddReactionAsync(
        ChatConnection connection, string channelId, string ts,
        string emoji, CancellationToken ct) =>
        // Incoming webhook não suporta reactions — no-op documentado.
        Task.CompletedTask;

    /// <inheritdoc />
    public Task SetStatusAsync(
        ChatConnection connection, string channelId, string threadTs,
        string status, CancellationToken ct) =>
        // Sem equivalente no Teams webhook — no-op documentado.
        Task.CompletedTask;
}