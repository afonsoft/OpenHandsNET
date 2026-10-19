using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Contracts;
using OpenHandsNET.Application.Tickets;

namespace OpenHandsNET.Infrastructure.Tickets;

/// <summary>
/// Base REST dos providers de ticket (SPEC-55): resolve token via
/// <see cref="ISecretStore"/> e envia request autenticado.
/// </summary>
public abstract class RestTicketProvider(
    IHttpClientFactory httpFactory,
    ISecretStore secrets,
    ILogger logger)
{
    /// <summary>Secret store (tokens/webhook secrets por org).</summary>
    protected ISecretStore Secrets => secrets;

    /// <inheritdoc cref="ITicketProvider.Kind" />
    public abstract TicketKind Kind { get; }

    /// <summary>Resolve o token no secret store da org da connection.</summary>
    protected async Task<string?> TokenAsync(
        TicketConnection c, CancellationToken ct) =>
        await secrets.ResolveAsync(
            "org", c.OrgId?.ToString() ?? "", c.TokenSecretRef, ct);

    /// <summary>Autentica o request no esquema do provider.</summary>
    protected abstract void ApplyAuth(
        HttpRequestMessage req, string token);

    /// <summary>Request autenticado com conteúdo JSON.</summary>
    protected async Task<HttpResponseMessage?> SendAsync(
        TicketConnection c, HttpMethod method, string url,
        object? body, CancellationToken ct)
    {
        var token = await TokenAsync(c, ct);
        if (token is null)
        {
            logger.LogWarning("{Kind}: token secret {Ref} unresolved",
                Kind, c.TokenSecretRef);
            return null;
        }
        var req = new HttpRequestMessage(method, url);
        ApplyAuth(req, token);
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }
        try
        {
            return await httpFactory.CreateClient().SendAsync(req, ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "{Kind} request {Url} failed", Kind, url);
            return null;
        }
    }

    /// <summary>GET JSON autenticado (null em erro).</summary>
    protected async Task<JsonDocument?> GetJsonAsync(
        TicketConnection c, string url, CancellationToken ct)
    {
        var resp = await SendAsync(c, HttpMethod.Get, url, null, ct);
        if (resp is null || !resp.IsSuccessStatusCode)
        {
            return null;
        }
        return await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    /// <summary>Igualdade em tempo constante (shared secrets).</summary>
    protected static bool FixedEquals(string expected, string? actual) =>
        actual is not null
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(actual));

    /// <summary>HMAC-SHA256 hex do payload.</summary>
    protected static string HmacHex(string key, byte[] payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(key), payload));
}

/// <summary>
/// Linear — GraphQL API + webhooks (SPEC-55).
/// Verify: <c>Linear-Signature</c> = hex(HMAC-SHA256(webhookSecret, body)).
/// Parse: <c>type=Issue action=update</c> (labels/assignee/state em
/// <c>updatedFrom</c>) e <c>type=Comment action=create</c>.
/// </summary>
public sealed class LinearTicketProvider(
    IHttpClientFactory httpFactory,
    ISecretStore secrets,
    ILogger<LinearTicketProvider> logger)
    : RestTicketProvider(httpFactory, secrets, logger), ITicketProvider
{
    private const string ApiBase = "https://api.linear.app/graphql";

    /// <inheritdoc />
    public override TicketKind Kind => TicketKind.Linear;

    /// <inheritdoc />
    protected override void ApplyAuth(
        HttpRequestMessage req, string token) =>
        req.Headers.Authorization = new AuthenticationHeaderValue(token);

    /// <inheritdoc />
    public async Task<bool> VerifyAsync(
        TicketConnection connection, string? signatureOrSecret,
        byte[] payload, CancellationToken ct)
    {
        if (connection.WebhookSecretRef is null)
        {
            return true; // sem segredo configurado, aceita (dev).
        }
        var secret = await Secrets.ResolveAsync(
            "org", connection.OrgId?.ToString() ?? "", connection.WebhookSecretRef, ct);
        return secret is not null
            && FixedEquals(HmacHex(secret, payload), signatureOrSecret);
    }

    /// <inheritdoc />
    public TicketWebhookEvent? ParseWebhook(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t)
                ? t.GetString() : null;
            var action = root.TryGetProperty("action", out var a)
                ? a.GetString() : null;
            if (!root.TryGetProperty("data", out var data))
            {
                return null;
            }
            var deliveryId = root.TryGetProperty("webhookId", out var w)
                ? w.GetString() : null;
            var createdAt = root.TryGetProperty("createdAt", out var ca)
                ? ca.GetString() : null;

            if (type == "Issue" && action == "update")
            {
                var key = data.TryGetProperty("identifier", out var id)
                    ? id.GetString() : null;
                if (key is null)
                {
                    return null;
                }
                string? addedLabel = null, newAssignee = null,
                    newState = null;
                if (root.TryGetProperty("updatedFrom", out var uf))
                {
                    // Mudança de label/assignee/state — updatedFrom tem
                    // o valor ANTERIOR; o novo está em data.
                    if (uf.TryGetProperty("labelIds", out _))
                    {
                        addedLabel = FirstLabelName(data);
                    }
                    if (uf.TryGetProperty("assigneeId", out _))
                    {
                        newAssignee = data
                            .TryGetProperty("assignee", out var asg)
                            ? asg.TryGetProperty("displayName", out var dn)
                                ? dn.GetString()
                                : asg.TryGetProperty("name", out var n)
                                    ? n.GetString() : null
                            : null;
                    }
                    if (uf.TryGetProperty("stateId", out _))
                    {
                        newState = data.TryGetProperty("state", out var st)
                            && st.TryGetProperty("name", out var sn)
                            ? sn.GetString() : null;
                    }
                }
                else if (ufBool(data))
                {
                    // Issue.create também chega como update parcial —
                    // trata label direto.
                    addedLabel = FirstLabelName(data);
                }

                return new TicketWebhookEvent(
                    "issue_updated", key,
                    deliveryId is null
                        ? null : $"{deliveryId}:{createdAt}",
                    addedLabel, newAssignee, newState, null,
                    ProjectKey: ProjectOf(key),
                    Title: Str(data, "title"),
                    Description: Str(data, "description"),
                    Url: Str(data, "url"),
                    Labels: LabelsOf(data),
                    State: null,
                    Assignee: null);
            }

            if (type == "Comment" && action == "create")
            {
                var body = Str(data, "body");
                var key = data.TryGetProperty("issue", out var iss)
                    ? Str(iss, "identifier") : null;
                if (key is null)
                {
                    return null;
                }
                return new TicketWebhookEvent(
                    "comment_created", key,
                    deliveryId is null
                        ? null : $"{deliveryId}:{createdAt}",
                    null, null, null, body,
                    ProjectKey: ProjectOf(key),
                    Title: null, Description: null,
                    Url: data.TryGetProperty("issue", out var i2)
                        ? Str(i2, "url") : null,
                    Labels: null, State: null, Assignee: null);
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<TicketIssue?> GetIssueAsync(
        TicketConnection connection, string issueKey, CancellationToken ct)
    {
        var resp = await SendAsync(connection, HttpMethod.Post,
            connection.ApiUrl is { Length: > 0 } ? connection.ApiUrl : ApiBase,
            new
            {
                query = """
                query($id: String!) {
                  issue(id: $id) {
                    identifier title description url
                    state { name }
                    assignee { displayName name }
                    labels { nodes { name } }
                    team { key }
                  }
                }
                """,
                variables = new { id = issueKey },
            }, ct);
        if (resp is null || !resp.IsSuccessStatusCode)
        {
            return null;
        }
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("data", out var d)
            || !d.TryGetProperty("issue", out var i)
            || i.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return new TicketIssue(
            Str(i, "identifier") ?? issueKey,
            Str(i, "title") ?? "", Str(i, "description"),
            Str(i, "url"), LabelsOf(i),
            i.TryGetProperty("state", out var s)
                ? Str(s, "name") : null,
            i.TryGetProperty("assignee", out var a)
                ? Str(a, "displayName") ?? Str(a, "name") : null,
            i.TryGetProperty("team", out var t)
                ? Str(t, "key") : null);
    }

    /// <inheritdoc />
    public async Task AddCommentAsync(
        TicketConnection connection, string issueKey, string body,
        CancellationToken ct)
    {
        // commentCreate exige o ID (uuid) da issue — resolve pelo identifier.
        var idResp = await SendAsync(connection, HttpMethod.Post,
            connection.ApiUrl is { Length: > 0 } ? connection.ApiUrl : ApiBase,
            new
            {
                query = "query($id: String!) { issue(id: $id) { id } }",
                variables = new { id = issueKey },
            }, ct);
        string? uuid = null;
        if (idResp is not null && idResp.IsSuccessStatusCode)
        {
            using var doc = await JsonDocument.ParseAsync(
                await idResp.Content.ReadAsStreamAsync(ct),
                cancellationToken: ct);
            uuid = doc.RootElement.TryGetProperty("data", out var d)
                && d.TryGetProperty("issue", out var i)
                ? Str(i, "id") : null;
        }
        if (uuid is null)
        {
            logger.LogWarning("linear: issue {Key} não encontrada", issueKey);
            return;
        }
        await SendAsync(connection, HttpMethod.Post,
            connection.ApiUrl is { Length: > 0 } ? connection.ApiUrl : ApiBase,
            new
            {
                query = """
                mutation($issueId: String!, $body: String!) {
                  commentCreate(input: { issueId: $issueId, body: $body }) {
                    success
                  }
                }
                """,
                variables = new { issueId = uuid, body },
            }, ct);
    }

    /// <inheritdoc />
    public async Task TransitionAsync(
        TicketConnection connection, string issueKey, string stateName,
        CancellationToken ct)
    {
        // Busca uuid da issue + team, depois o stateId pelo nome.
        var resp = await SendAsync(connection, HttpMethod.Post,
            connection.ApiUrl is { Length: > 0 } ? connection.ApiUrl : ApiBase,
            new
            {
                query = """
                query($id: String!) {
                  issue(id: $id) {
                    id
                    team { id states { nodes { id name } } }
                  }
                }
                """,
                variables = new { id = issueKey },
            }, ct);
        if (resp is null || !resp.IsSuccessStatusCode)
        {
            return;
        }
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("data", out var d)
            || !d.TryGetProperty("issue", out var i)
            || i.ValueKind == JsonValueKind.Null)
        {
            return;
        }
        var uuid = Str(i, "id");
        string? stateId = null;
        if (i.TryGetProperty("team", out var t)
            && t.TryGetProperty("states", out var st)
            && st.TryGetProperty("nodes", out var nodes))
        {
            foreach (var s in nodes.EnumerateArray())
            {
                if (string.Equals(Str(s, "name"), stateName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    stateId = Str(s, "id");
                    break;
                }
            }
        }
        if (uuid is null || stateId is null)
        {
            logger.LogWarning(
                "linear: estado {State} não achado p/ {Key}",
                stateName, issueKey);
            return;
        }
        await SendAsync(connection, HttpMethod.Post,
            connection.ApiUrl is { Length: > 0 } ? connection.ApiUrl : ApiBase,
            new
            {
                query = """
                mutation($id: String!, $stateId: String!) {
                  issueUpdate(id: $id, input: { stateId: $stateId }) {
                    success
                  }
                }
                """,
                variables = new { id = uuid, stateId },
            }, ct);
    }

    /// <inheritdoc />
    public async Task<(bool Ok, string? Detail)> TestConnectionAsync(
        TicketConnection connection, CancellationToken ct)
    {
        var resp = await SendAsync(connection, HttpMethod.Post,
            connection.ApiUrl is { Length: > 0 } ? connection.ApiUrl : ApiBase,
            new { query = "{ viewer { id name } }" }, ct);
        if (resp is null)
        {
            return (false, "request failed");
        }
        if (!resp.IsSuccessStatusCode)
        {
            return (false, $"HTTP {(int)resp.StatusCode}");
        }
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct);
        var name = doc.RootElement.TryGetProperty("data", out var d)
            && d.TryGetProperty("viewer", out var v)
            ? Str(v, "name") : null;
        return (name is not null, name);
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v)
            && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string? ProjectOf(string identifier)
    {
        var dash = identifier.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 ? identifier[..dash] : null;
    }

    private static bool ufBool(JsonElement data) =>
        data.TryGetProperty("labels", out var l)
        && l.ValueKind == JsonValueKind.Object
        && l.TryGetProperty("nodes", out var n)
        && n.GetArrayLength() > 0;

    private static string? FirstLabelName(JsonElement data) =>
        LabelsOf(data) is { Count: > 0 } l ? l[0] : null;

    private static IReadOnlyList<string> LabelsOf(JsonElement data)
    {
        var list = new List<string>();
        // Linear manda labels como connection { nodes: [...] } ou array.
        if (data.TryGetProperty("labels", out var l))
        {
            var nodes = l.ValueKind == JsonValueKind.Object
                && l.TryGetProperty("nodes", out var n) ? n : l;
            if (nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in nodes.EnumerateArray())
                {
                    if (Str(x, "name") is { } name)
                    {
                        list.Add(name);
                    }
                }
            }
        }
        return list;
    }
}

/// <summary>
/// Jira Cloud — REST v3 + webhooks (SPEC-55).
/// Verify: segredo compartilhado via <c>X-OpenHandsNet-Secret</c> ou
/// <c>?secret=</c> (fixed-time). Parse:
/// <c>jira:issue_updated</c> (changelog) e <c>comment_created</c>.
/// </summary>
public sealed class JiraTicketProvider(
    IHttpClientFactory httpFactory,
    ISecretStore secrets,
    ILogger<JiraTicketProvider> logger)
    : RestTicketProvider(httpFactory, secrets, logger), ITicketProvider
{
    /// <inheritdoc />
    public override TicketKind Kind => TicketKind.Jira;

    /// <inheritdoc />
    protected override void ApplyAuth(
        HttpRequestMessage req, string token)
    {
        // Jira Cloud: Basic base64(email:api-token) — token pode vir
        // como "email:token" ou token sozinho (server-side alias).
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                    token.Contains(':', StringComparison.Ordinal)
                        ? token : $":{token}")));
    }

    private static string Base(TicketConnection c) =>
        c.ApiUrl.TrimEnd('/') + "/rest/api/3";

    /// <inheritdoc />
    public async Task<bool> VerifyAsync(
        TicketConnection connection, string? signatureOrSecret,
        byte[] payload, CancellationToken ct)
    {
        if (connection.WebhookSecretRef is null)
        {
            return true;
        }
        var secret = await Secrets.ResolveAsync(
            "org", connection.OrgId?.ToString() ?? "", connection.WebhookSecretRef, ct);
        return secret is not null && FixedEquals(secret, signatureOrSecret);
    }

    /// <inheritdoc />
    public TicketWebhookEvent? ParseWebhook(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var eventType = Str(root, "webhookEvent");
            if (eventType is null)
            {
                return null;
            }
            var deliveryId = root.TryGetProperty("timestamp", out var ts)
                ? ts.GetRawText() : null;

            if (eventType == "jira:issue_updated"
                && root.TryGetProperty("issue", out var issue))
            {
                var key = Str(issue, "key");
                if (key is null)
                {
                    return null;
                }
                string? addedLabel = null, newAssignee = null,
                    newState = null;
                if (root.TryGetProperty("changelog", out var cl)
                    && cl.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        switch (Str(item, "field"))
                        {
                            case "labels":
                                addedLabel = Str(item, "toString")
                                    ?? Str(item, "to");
                                break;
                            case "assignee":
                                newAssignee = Str(item, "toString")
                                    ?? Str(item, "to");
                                break;
                            case "status":
                                newState = Str(item, "toString")
                                    ?? Str(item, "to");
                                break;
                        }
                    }
                }
                var f = issue.TryGetProperty("fields", out var ff)
                    ? ff : default;
                return new TicketWebhookEvent(
                    "issue_updated", key, deliveryId,
                    addedLabel, newAssignee, newState, null,
                    ProjectKey: ProjectOf(key),
                    Title: Str(f, "summary"),
                    Description: AdfToText(f, "description"),
                    Url: UrlOf(issue),
                    Labels: LabelsOf(f),
                    State: f.TryGetProperty("status", out var s)
                        ? Str(s, "name") : null,
                    Assignee: f.TryGetProperty("assignee", out var a)
                        ? Str(a, "displayName") : null);
            }

            if (eventType == "comment_created"
                && root.TryGetProperty("issue", out var iss2))
            {
                var key = Str(iss2, "key");
                if (key is null)
                {
                    return null;
                }
                var body = root.TryGetProperty("comment", out var cm)
                    ? AdfToText(cm, "body") : null;
                return new TicketWebhookEvent(
                    "comment_created", key, deliveryId,
                    null, null, null, body,
                    ProjectKey: ProjectOf(key),
                    Title: null, Description: null,
                    Url: UrlOf(iss2),
                    Labels: null, State: null, Assignee: null);
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<TicketIssue?> GetIssueAsync(
        TicketConnection connection, string issueKey, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            connection, $"{Base(connection)}/issue/{Enc(issueKey)}?fields=summary," +
               "description,labels,status,assignee", ct);
        if (doc is null)
        {
            return null;
        }
        var root = doc.RootElement;
        var f = root.TryGetProperty("fields", out var ff) ? ff : default;
        return new TicketIssue(
            Str(root, "key") ?? issueKey,
            Str(f, "summary") ?? "",
            AdfToText(f, "description"), UrlOf(root),
            LabelsOf(f),
            f.TryGetProperty("status", out var s)
                ? Str(s, "name") : null,
            f.TryGetProperty("assignee", out var a)
                ? Str(a, "displayName") : null,
            ProjectOf(issueKey));
    }

    /// <inheritdoc />
    public async Task AddCommentAsync(
        TicketConnection connection, string issueKey, string body,
        CancellationToken ct)
    {
        // Jira Cloud exige comentário em ADF (Atlassian Document Format).
        await SendAsync(connection, HttpMethod.Post,
            $"{Base(connection)}/issue/{Enc(issueKey)}/comment",
            new
            {
                body = new
                {
                    type = "doc",
                    version = 1,
                    content = new[]
                    {
                        new
                        {
                            type = "paragraph",
                            content = new[]
                            {
                                new { type = "text", text = body },
                            },
                        },
                    },
                },
            }, ct);
    }

    /// <inheritdoc />
    public async Task TransitionAsync(
        TicketConnection connection, string issueKey, string stateName,
        CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            connection, $"{Base(connection)}/issue/{Enc(issueKey)}/transitions", ct);
        if (doc is null
            || !doc.RootElement.TryGetProperty("transitions", out var trs))
        {
            return;
        }
        string? transitionId = null;
        foreach (var tr in trs.EnumerateArray())
        {
            var name = tr.TryGetProperty("to", out var to)
                ? Str(to, "name") : Str(tr, "name");
            if (string.Equals(name, stateName,
                    StringComparison.OrdinalIgnoreCase))
            {
                transitionId = Str(tr, "id");
                break;
            }
        }
        if (transitionId is null)
        {
            logger.LogWarning(
                "jira: transição p/ {State} não achada em {Key}",
                stateName, issueKey);
            return;
        }
        await SendAsync(connection, HttpMethod.Post,
            $"{Base(connection)}/issue/{Enc(issueKey)}/transitions",
            new { transition = new { id = transitionId } }, ct);
    }

    /// <inheritdoc />
    public async Task<(bool Ok, string? Detail)> TestConnectionAsync(
        TicketConnection connection, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            connection, $"{Base(connection)}/myself", ct);
        if (doc is null)
        {
            return (false, "auth or request failed");
        }
        var name = Str(doc.RootElement, "displayName")
            ?? Str(doc.RootElement, "emailAddress");
        return (name is not null, name);
    }

    private static string Enc(string s) => Uri.EscapeDataString(s);

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string? ProjectOf(string key)
    {
        var dash = key.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 ? key[..dash] : null;
    }

    private static string? UrlOf(JsonElement issue)
    {
        // issue.self é a URL REST — link pro humano é o host base.
        var self = Str(issue, "self");
        if (self is null)
        {
            return null;
        }
        var key = Str(issue, "key");
        try
        {
            var host = new Uri(self).GetLeftPart(UriPartial.Authority);
            return key is null ? host : $"{host}/browse/{key}";
        }
        catch (UriFormatException)
        {
            return self;
        }
    }

    private static IReadOnlyList<string> LabelsOf(JsonElement fields)
    {
        var list = new List<string>();
        if (fields.ValueKind == JsonValueKind.Object
            && fields.TryGetProperty("labels", out var l)
            && l.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in l.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.String)
                {
                    list.Add(x.GetString()!);
                }
            }
        }
        return list;
    }

    /// <summary>
    /// ADF → texto plano (Jira manda description/comment body em
    /// Atlassian Document Format). Extrai text nodes recursivamente.
    /// </summary>
    private static string? AdfToText(JsonElement parent, string prop)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(prop, out var doc)
            || doc.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var sb = new StringBuilder();
        Walk(doc);
        var s = sb.ToString().Trim();
        return s.Length == 0 ? null : s;

        void Walk(JsonElement n)
        {
            if (n.ValueKind == JsonValueKind.Object)
            {
                if (n.TryGetProperty("type", out var t)
                    && t.GetString() == "text"
                    && n.TryGetProperty("text", out var txt))
                {
                    sb.Append(txt.GetString());
                }
                if (n.TryGetProperty("content", out var c)
                    && c.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in c.EnumerateArray())
                    {
                        Walk(child);
                    }
                    if (n.TryGetProperty("type", out var t2)
                        && (t2.GetString() == "paragraph"
                            || t2.GetString() == "heading"))
                    {
                        sb.Append('\n');
                    }
                }
            }
        }
    }
}