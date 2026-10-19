using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using OpenHandsNET.Application.ChatOps;
using OpenHandsNET.Application.Identity;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// SPEC-54 — ChatOps Slack: webhooks públicos
/// <c>/api/v1/webhooks/chat/slack/{events,commands,interactions}</c> +
/// admin <c>/orgs/{org}/chat-connections</c> + canal→repo config +
/// test de connection.
/// </summary>
public static class ChatOpsEndpoints
{
    private const int MaxWebhookPayload = 256 * 1024;

    /// <summary>Maps ChatOps endpoints.</summary>
    public static IEndpointRouteBuilder MapChatOpsEndpoints(
        this IEndpointRouteBuilder app)
    {
        // ---------- Inbound público ----------

        app.MapPost("/api/v1/webhooks/chat/slack/events", async (
            HttpRequest req, IChatOpsService svc, CancellationToken ct) =>
        {
            if ((req.ContentLength ?? 0) > MaxWebhookPayload)
                return Results.StatusCode(413);
            var payload = await ReadBody(req, ct);
            var (result, challenge) = await svc.HandleEventAsync(
                req.Headers["X-Slack-Signature"].FirstOrDefault(),
                req.Headers["X-Slack-Request-Timestamp"].FirstOrDefault(),
                payload, ct);
            if (result == ChatOpsResult.Challenge)
            {
                return Results.Text(
                    JsonSerializer.Serialize(new { challenge }),
                    "application/json");
            }
            return MapResult(result);
        });

        app.MapPost("/api/v1/webhooks/chat/slack/commands", async (
            HttpRequest req, IChatOpsService svc, CancellationToken ct) =>
        {
            if ((req.ContentLength ?? 0) > MaxWebhookPayload)
                return Results.StatusCode(413);
            var payload = await ReadBody(req, ct);
            var form = ParseForm(payload);
            var (result, reply) = await svc.HandleCommandAsync(
                req.Headers["X-Slack-Signature"].FirstOrDefault(),
                req.Headers["X-Slack-Request-Timestamp"].FirstOrDefault(),
                payload, form, ct);
            if (result == ChatOpsResult.InvalidSignature)
            {
                return Results.Unauthorized();
            }
            if (result == ChatOpsResult.NoConnection)
            {
                return Results.NotFound(new { error = "no_connection" });
            }
            return Results.Ok(new
            {
                response_type = "ephemeral",
                text = reply ?? "ok",
            });
        });

        app.MapPost("/api/v1/webhooks/chat/slack/interactions", async (
            HttpRequest req, IChatOpsService svc, CancellationToken ct) =>
        {
            if ((req.ContentLength ?? 0) > MaxWebhookPayload)
                return Results.StatusCode(413);
            var payload = await ReadBody(req, ct);
            var form = ParseForm(payload);
            var inner = form.TryGetValue("payload", out var p)
                ? Encoding.UTF8.GetBytes(p) : [];
            var result = await svc.HandleInteractionAsync(
                req.Headers["X-Slack-Signature"].FirstOrDefault(),
                req.Headers["X-Slack-Request-Timestamp"].FirstOrDefault(),
                payload, inner, ct);
            return MapResult(result);
        });

        // ---------- Admin (autenticado) ----------

        var conns = app.MapGroup("/api/v1/orgs/{org_id:guid}/chat-connections")
            .RequireAuthorization("perm:manage_integrations");

        conns.MapGet("", async (
            Guid org_id, IChatConnectionService svc,
            CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(org_id, ct)));

        conns.MapPost("", async (
            Guid org_id, [FromBody] ChatConnectionRequest req,
            ICurrentUserAccessor current, IChatConnectionService svc,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            var conn = await svc.CreateAsync(
                org_id, req.TeamId, req.BotTokenSecretRef,
                req.SigningSecretRef, req.BotUserId,
                req.DefaultChannelId, user.UserId.ToString(), ct);
            return Results.Ok(conn);
        });

        conns.MapDelete("/{id:guid}", async (
            Guid id, IChatConnectionService svc, CancellationToken ct) =>
            await svc.DeleteAsync(id, ct)
                ? Results.NoContent() : Results.NotFound());

        conns.MapGet("/{id:guid}/channels", async (
            Guid id, IChatConnectionService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListChannelConfigsAsync(id, ct)));

        // SPEC-54 RF-004: canal→repo + auto-triage.
        app.MapPost("/api/v1/chat/channels/{connectionId:guid}/config",
            async (Guid connectionId, [FromBody] ChatChannelConfigRequest req,
                IChatConnectionService svc, CancellationToken ct) =>
            Results.Ok(await svc.SetChannelConfigAsync(
                connectionId, req.ChannelId, req.SelectedRepository,
                req.SelectedBranch, req.AutoTriage, ct)))
            .RequireAuthorization("perm:manage_integrations");

        // SPEC-54: diagnóstico — auth.test via IChatSink (postMessage no-op
        // aqui; test completo de token fica no SlackChatSink/test job).
        app.MapPost("/api/v1/chat/connections/{id:guid}/test", async (
            Guid id, IChatConnectionService conns2, IChatSink sink,
            CancellationToken ct) =>
        {
            var conn = await conns2.GetAsync(id, ct);
            if (conn is null)
                return Results.NotFound();
            var ts = await sink.PostMessageAsync(
                conn, conn.DefaultChannelId ?? "general", null,
                ":mag: OpenHandsNET connection test", ct);
            return Results.Ok(new { ok = ts is not null, ts });
        }).RequireAuthorization("perm:manage_integrations");

        return app;
    }

    private static async Task<byte[]> ReadBody(
        HttpRequest req, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static Dictionary<string, string> ParseForm(byte[] payload)
    {
        var dict = new Dictionary<string, string>();
        var text = Encoding.UTF8.GetString(payload);
        foreach (var pair in text.Split('&',
            StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0)
            {
                continue;
            }
            dict[Uri.UnescapeDataString(
                pair[..idx].Replace('+', ' '))] =
                Uri.UnescapeDataString(
                    pair[(idx + 1)..].Replace('+', ' '));
        }
        return dict;
    }

    private static IResult MapResult(ChatOpsResult result) => result switch
    {
        ChatOpsResult.InvalidSignature => Results.Unauthorized(),
        ChatOpsResult.NoConnection =>
            Results.NotFound(new { error = "no_connection" }),
        ChatOpsResult.Duplicate => Results.Ok(new { status = "duplicate" }),
        ChatOpsResult.Ignored => Results.Ok(new { status = "ignored" }),
        _ => Results.Ok(new { status = "processed" }),
    };
}

/// <summary>Create chat connection request.</summary>
public sealed record ChatConnectionRequest(
    string TeamId,
    string BotTokenSecretRef,
    string SigningSecretRef,
    string? BotUserId = null,
    string? DefaultChannelId = null);

/// <summary>Canal→repo config request (SPEC-54 RF-004).</summary>
public sealed record ChatChannelConfigRequest(
    string ChannelId,
    string? SelectedRepository = null,
    string? SelectedBranch = null,
    bool AutoTriage = false);