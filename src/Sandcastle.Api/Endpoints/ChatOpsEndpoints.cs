using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.ChatOps;
using Sandcastle.Application.Identity;

namespace Sandcastle.Api.Endpoints;

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

        app.MapPost("/api/v1/webhooks/chat/slack/events",
            SlackEventsAsync).WithTags(EndpointTags.Api);

        app.MapPost("/api/v1/webhooks/chat/slack/commands",
            SlackCommandsAsync).WithTags(EndpointTags.Api);

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
            Results.Ok((await svc.ListAsync(org_id, ct))
                .Select(MaskConnection).ToList()));

        conns.MapPost("", async (
            Guid org_id, [FromBody] ChatConnectionRequest req,
            ICurrentUserAccessor current, IChatConnectionService svc,
            HttpContext http, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
                return Results.Unauthorized();
            // SPEC-151 RF-003: provider picker — slack (default) ou
            // teams (Incoming Webhook URL, sem bot token/signing).
            var kind = ChatKindExtensions.Parse(req.Provider)
                ?? ChatKind.Slack;
            if (kind == ChatKind.Teams
                && (string.IsNullOrWhiteSpace(req.WebhookUrl)
                    || !Uri.TryCreate(req.WebhookUrl, UriKind.Absolute,
                        out var u)
                    || u.Scheme != Uri.UriSchemeHttps))
            {
                return SandcastleProblemFactory.Create(http, 400,
                    "teams connections require an https webhook_url");
            }
            var conn = await svc.CreateAsync(
                org_id, req.TeamId, req.BotTokenSecretRef,
                req.SigningSecretRef, req.BotUserId,
                req.DefaultChannelId, user.UserId.ToString(), ct,
                kind, req.WebhookUrl);
            return Results.Ok(MaskConnection(conn));
        });

        conns.MapDelete("/{id:guid}", async (
            Guid org_id, Guid id, IChatConnectionService svc,
            CancellationToken ct) =>
            await svc.DeleteAsync(id, org_id, ct)
                ? Results.NoContent() : Results.NotFound());

        conns.MapGet("/{id:guid}/channels", async (
            Guid org_id, Guid id, IChatConnectionService svc,
            CancellationToken ct) =>
            await svc.ListChannelConfigsAsync(id, org_id, ct) is { } list
                ? Results.Ok(list) : Results.NotFound());

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
        app.MapPost("/api/v1/chat/connections/{id:guid}/test",
            TestConnectionAsync)
            .RequireAuthorization("perm:manage_integrations");

        // SPEC-151 RF-003: rota canônica {provider}-aware (Teams posta
        // card de teste no webhook; Slack posta no canal default).
        app.MapPost("/api/v1/chat-connections/{id:guid}/test",
            TestConnectionAsync)
            .RequireAuthorization("perm:manage_integrations");

        return app;
    }

    private static async Task<IResult> TestConnectionAsync(
        Guid id, IChatConnectionService conns, IChatSink sink,
        CancellationToken ct)
    {
        var conn = await conns.GetAsync(id, ct);
        if (conn is null)
        {
            return Results.NotFound();
        }
        var ts = await sink.PostMessageAsync(
            conn, conn.DefaultChannelId ?? "general", null,
            ":mag: Sandcastle connection test", ct);
        return Results.Ok(new { ok = ts is not null, ts });
    }

    /// <summary>
    /// SPEC-151: <c>webhook_url</c> é credencial — serializa mascarada
    /// (scheme + sufixo; nunca a URL completa).
    /// </summary>
    private static object MaskConnection(ChatConnection c) => new
    {
        c.Id,
        org_id = c.OrgId,
        kind = c.Kind.ToWire(),
        provider = c.Kind.ToWire(),
        c.TeamId,
        c.BotTokenSecretRef,
        c.SigningSecretRef,
        c.BotUserId,
        c.DefaultChannelId,
        c.CreatedByUserId,
        c.CreatedAt,
        webhook_url = MaskWebhook(c.WebhookUrl),
        has_webhook_url = c.WebhookUrl is not null,
    };

    /// <summary>Mostra só scheme + últimos 4 — nunca a URL inteira.</summary>
    private static string? MaskWebhook(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }
        var tail = url.Length > 4 ? url[^4..] : url;
        return $"https://***{tail}";
    }

    private static async Task<IResult> SlackEventsAsync(
        HttpRequest req, IChatOpsService svc, CancellationToken ct)
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
    }

    private static async Task<IResult> SlackCommandsAsync(
        HttpRequest req, IChatOpsService svc, CancellationToken ct)
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

/// <summary>
/// Create chat connection request. SPEC-151: <paramref name="Provider"/>
/// = <c>slack</c> (default) | <c>teams</c> — Teams exige
/// <paramref name="WebhookUrl"/> e ignora os secret refs.
/// </summary>
public sealed record ChatConnectionRequest(
    string TeamId,
    string BotTokenSecretRef,
    string SigningSecretRef,
    string? BotUserId = null,
    string? DefaultChannelId = null,
    string? Provider = null,
    string? WebhookUrl = null);

/// <summary>Canal→repo config request (SPEC-54 RF-004).</summary>
public sealed record ChatChannelConfigRequest(
    string ChannelId,
    string? SelectedRepository = null,
    string? SelectedBranch = null,
    bool AutoTriage = false);