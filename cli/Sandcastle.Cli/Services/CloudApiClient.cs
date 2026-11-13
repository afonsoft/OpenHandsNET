using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Sandcastle.Cli.Services;

/// <summary>
/// REST client tipado do servidor (V1). Resolução de URL/API key:
/// <c>--server-url</c> > SANDCASTLE_SERVER_URL > SANDCASTLE_CLOUD_URL >
/// credencial salva > default local.
/// </summary>
public sealed class CloudApiClient
{
    private const string DefaultUrl = CliDefaults.DefaultServerUrl;
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public CloudApiClient(HttpClient http, string baseUrl, string? apiKey = null)
    {
        _http = http;
        BaseUrl = baseUrl.TrimEnd('/');
        if (apiKey is not null)
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    public string BaseUrl { get; }

    /// <summary>Resolve o servidor efetivo (flag > env > store > default).</summary>
    public static async Task<CloudApiClient> CreateAsync(
        string? serverUrl, CredentialStore store,
        HttpClient? http = null, CancellationToken ct = default)
    {
        var url = serverUrl
            ?? Environment.GetEnvironmentVariable("SANDCASTLE_SERVER_URL")
            ?? Environment.GetEnvironmentVariable("SANDCASTLE_CLOUD_URL")
            ?? DefaultUrl;
        var saved = await store.GetAsync(url, ct);
        var key = Environment.GetEnvironmentVariable("SANDCASTLE_API_KEY")
            ?? saved?.ApiKey ?? saved?.AccessToken;
        return new CloudApiClient(http ?? new HttpClient(), url, key);
    }

    /// <summary>
    /// POST /app-conversations → start-task. SPEC-146:
    /// <paramref name="repositories"/> (máx 4, índice 0 = primário) →
    /// <c>selected_repositories</c> + primário espelhado no legado
    /// <c>selected_repository</c>. String solitária continua aceita.
    /// </summary>
    public async Task<JsonElement> CreateConversationAsync(
        string? initialMessage, IReadOnlyList<string>? repositories,
        bool run = true, bool confirmationMode = false, string? model = null,
        CancellationToken ct = default)
    {
        object? initial = initialMessage is null ? null : new
        {
            role = "user",
            content = new[] { new { type = "text", text = initialMessage } },
            run,
        };
        var resp = await _http.PostAsJsonAsync(
            $"{BaseUrl}/api/v1/app-conversations", new
            {
                initial_message = initial,
                selected_repository = repositories is { Count: > 0 }
                    ? repositories[0] : null,
                selected_repositories =
                    repositories is { Count: > 0 } ? repositories : null,
                confirmation_mode = confirmationMode,
                llm_model = model,
            }, ct);
        await EnsureAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public async Task<JsonElement?> GetStartTaskAsync(
        string taskId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/api/v1/app-conversations/start-tasks/{taskId}", ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<JsonElement>(ct) : null;
    }

    public async Task<JsonElement> ListConversationsAsync(
        CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<JsonElement>(
            $"{BaseUrl}/api/v1/app-conversations/search?limit=50", ct);

    public async Task<JsonElement?> GetConversationAsync(
        string id, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/api/v1/app-conversations/{id}", ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<JsonElement>(ct) : null;
    }

    public async Task<JsonElement> GetEventsAsync(
        string conversationId, string? pageId = null,
        CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<JsonElement>(
            $"{BaseUrl}/api/v1/conversation/{conversationId}/events/search"
            + $"?limit=100{(pageId is null ? "" : $"&page_id={pageId}")}", ct);

    public async Task SendMessageAsync(
        string conversationId, string text, CancellationToken ct = default) =>
        await EnsureAsync(await _http.PostAsJsonAsync(
            $"{BaseUrl}/api/v1/app-conversations/{conversationId}/send-message",
            new
            {
                role = "user",
                content = new[] { new { type = "text", text } },
                run = true,
            }, ct), ct);

    public async Task DeleteConversationAsync(
        string id, CancellationToken ct = default) =>
        await EnsureAsync(await _http.DeleteAsync(
            $"{BaseUrl}/api/v1/app-conversations/{id}", ct), ct);

    /// <summary>SPEC-31: catálogo OmniRoute (SPEC-23 RF-005).</summary>
    public async Task<JsonElement> GetModelsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<JsonElement>(
            $"{BaseUrl}/api/v1/models", ct);

    /// <summary>SPEC-31: catálogo efetivo de tools da conversa (SPEC-25/29).</summary>
    public async Task<JsonElement> GetToolsAsync(
        string conversationId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<JsonElement>(
            $"{BaseUrl}/api/v1/app-conversations/{conversationId}/tools", ct);

    /// <summary>SPEC-31: contagem de conversas (número puro).</summary>
    public async Task<long> CountConversationsAsync(
        CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<long>(
            $"{BaseUrl}/api/v1/app-conversations/count", ct);

    /// <summary>SPEC-31: MCP servers configurados (SPEC-07).</summary>
    public async Task<JsonElement> GetMcpServersAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<JsonElement>(
            $"{BaseUrl}/api/v1/mcp/servers", ct);

    /// <summary>SPEC-31: probe de um MCP server (SPEC-07 /mcp/test).</summary>
    public async Task<JsonElement> ProbeMcpAsync(
        JsonElement serverConfig, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"{BaseUrl}/api/v1/mcp/test", new { server = serverConfig }, ct);
        await EnsureAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    /// <summary>SPEC-31 RF-002: readiness para doctor --preflight.</summary>
    public async Task<JsonElement> GetReadinessAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<JsonElement>(
            $"{BaseUrl}/api/v1/readiness", ct);

    /// <summary>SPEC-31: usage agregado da org (SPEC-20; requer billing perm).</summary>
    public async Task<JsonElement?> GetOrgUsageAsync(
        Guid orgId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/api/v1/organizations/{orgId}/usage", ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<JsonElement>(ct) : null;
    }

    /// <summary>SPEC-31: orgs do usuário (para resolver a org do stats).</summary>
    public async Task<JsonElement> ListOrgsAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/api/v1/organizations", ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<JsonElement>(ct)
            : default;
    }

    /// <summary>
    /// SPEC-31: stream paginado de eventos duráveis para <c>export</c>
    /// (nunca inclui streaming_delta — pseudo-kind efêmero).
    /// </summary>
    public async IAsyncEnumerable<JsonElement> ExportEventsAsync(
        string conversationId,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct = default)
    {
        string? cursor = null;
        do
        {
            var page = await GetEventsAsync(conversationId, cursor, ct);
            if (page.TryGetProperty("items", out var items))
            {
                foreach (var e in items.EnumerateArray())
                {
                    yield return e;
                }
            }

            cursor = page.TryGetProperty("next_page_id", out var n)
                && n.ValueKind == JsonValueKind.String
                ? n.GetString() : null;
        }
        while (cursor is not null);
    }

    /// <summary>
    /// SPEC-56 RF-004: GET /app-conversations/{id}/download → stream do
    /// zip de trajetória (events.jsonl + metadata + diff + summary).
    /// Caller dispõe da response (o stream do body vive junto).
    /// </summary>
    public async Task<HttpResponseMessage> DownloadTrajectoryAsync(
        string conversationId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/api/v1/app-conversations/{conversationId}/download",
            HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureAsync(resp, ct);
        return resp;
    }

    /// <summary>SPEC-52: lista scm-connections de uma org.</summary>
    public async Task<JsonElement> ListScmConnectionsAsync(
        string orgId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/api/v1/orgs/{orgId}/scm-connections", ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<JsonElement>(ct)
            : default;
    }

    /// <summary>SPEC-52: gera o manifest do GitHub App (passo 1 do fluxo).</summary>
    public async Task<JsonElement> BuildScmManifestAsync(
        string redirectUri, string? appName, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"{BaseUrl}/api/v1/scm/github/manifest",
            new { redirect_uri = redirectUri, app_name = appName }, Json, ct);
        await EnsureAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    /// <summary>SPEC-54: lista chat-connections (Slack) de uma org.</summary>
    public async Task<JsonElement> ListChatConnectionsAsync(
        string orgId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/api/v1/orgs/{orgId}/chat-connections", ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<JsonElement>(ct)
            : default;
    }

    /// <summary>SPEC-54: ping na chat connection (diagnóstico).</summary>
    public async Task<JsonElement> TestChatConnectionAsync(
        string connectionId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync(
            $"{BaseUrl}/api/v1/chat/connections/{connectionId}/test",
            null, ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<JsonElement>(ct)
            : default;
    }

    /// <summary>SPEC-55: lista ticket-connections de uma org.</summary>
    public async Task<JsonElement> ListTicketConnectionsAsync(
        string orgId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/api/v1/orgs/{orgId}/ticket-connections", ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<JsonElement>(ct)
            : default;
    }

    /// <summary>SPEC-55: diagnóstico da ticket connection.</summary>
    public async Task<JsonElement> TestTicketConnectionAsync(
        string connectionId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync(
            $"{BaseUrl}/api/v1/tickets/connections/{connectionId}/test",
            null, ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<JsonElement>(ct)
            : default;
    }

    /// <summary>SPEC-55: dispara run manual por issue.</summary>
    public async Task<JsonElement> RunTicketAsync(
        string connectionId, string issueKey, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync(
            $"{BaseUrl}/api/v1/tickets/connections/{connectionId}/run",
            new { issue_key = issueKey }, Json, ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<JsonElement>(ct)
            : default;
    }

    /// <summary>SPEC-57: service users da org.</summary>
    public async Task<JsonElement> ListServiceUsersAsync(
        string orgId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/api/v1/orgs/{orgId}/service-users", ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<JsonElement>(ct)
            : default;
    }

    /// <summary>SPEC-57: cria service user.</summary>
    public async Task<bool> CreateServiceUserAsync(
        string orgId, string name, string role, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync(
            $"{BaseUrl}/api/v1/orgs/{orgId}/service-users",
            new { name, role }, Json, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>SPEC-57: emite svc_* — retorna plaintext ou null.</summary>
    public async Task<string?> CreateServiceUserKeyAsync(
        string orgId, string serviceUserId, IReadOnlyList<string> scopes,
        CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync(
            $"{BaseUrl}/api/v1/orgs/{orgId}/service-users/{serviceUserId}/keys",
            new { scopes }, Json, ct);
        if (!resp.IsSuccessStatusCode)
            return null;
        var body = await resp.Content
            .ReadFromJsonAsync<JsonElement>(ct);
        return body.TryGetProperty("key", out var k) ? k.GetString() : null;
    }

    // ---- marketplace (SPEC-148) ---------------------------------------

    /// <summary>GET /marketplace/entries — catálogo (items[]).</summary>
    public async Task<JsonElement> GetMarketplaceEntriesAsync(
        string? q = null, string? kind = null, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/v1/marketplace/entries?x=1"
            + (q is { } s ? $"&q={Uri.EscapeDataString(s)}" : "")
            + (kind is { } k ? $"&kind={Uri.EscapeDataString(k)}" : "");
        return await _http.GetFromJsonAsync<JsonElement>(url, Json, ct);
    }

    /// <summary>GET /marketplace/entries/{id} — detalhe ou null (404).</summary>
    public async Task<JsonElement?> GetMarketplaceEntryAsync(
        string id, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/api/v1/marketplace/entries/{Uri.EscapeDataString(id)}",
            ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<JsonElement>(ct) : null;
    }

    /// <summary>GET /marketplace/installed — installs visíveis (items[]).</summary>
    public async Task<JsonElement> GetMarketplaceInstalledAsync(
        string? scope = null, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<JsonElement>(
            $"{BaseUrl}/api/v1/marketplace/installed"
            + (scope is { } s ? $"?scope={s}" : ""), Json, ct);

    /// <summary>
    /// POST /marketplace/install → (status, body). Sem EnsureAsync — o
    /// caller distingue 403/409/422/502 para mensagens úteis.
    /// </summary>
    public async Task<(int Status, JsonElement Body)> InstallMarketplaceAsync(
        string entryId, string scope, string? scopeId = null,
        string? version = null, bool acceptPermissions = false,
        CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"{BaseUrl}/api/v1/marketplace/install", new
            {
                entry_id = entryId,
                version,
                scope,
                scope_id = scopeId,
                accept_permissions = acceptPermissions,
            }, Json, ct);
        var body = await resp.Content
            .ReadFromJsonAsync<JsonElement>(ct);
        return ((int)resp.StatusCode, body);
    }

    /// <summary>DELETE /marketplace/installed/{id} → status code.</summary>
    public async Task<int> RemoveMarketplaceAsync(
        Guid id, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync(
            $"{BaseUrl}/api/v1/marketplace/installed/{id}", ct);
        return (int)resp.StatusCode;
    }

    /// <summary>POST /marketplace/upgrade/{id} → status code.</summary>
    public async Task<int> UpgradeMarketplaceAsync(
        Guid id, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync(
            $"{BaseUrl}/api/v1/marketplace/upgrade/{id}", null, ct);
        return (int)resp.StatusCode;
    }

    // ---- handoff (SPEC-150) -------------------------------------------

    /// <summary>
    /// SPEC-150 RF-001: POST /conversations/handoff → start-task (body
    /// cru). Erros sobem via <see cref="EnsureAsync"/> com o status.
    /// </summary>
    public async Task<JsonElement> CreateHandoffAsync(
        string repository, string baseSha, string? baseBranch,
        string diffPatch, string? message, object? metadata,
        CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"{BaseUrl}/api/v1/conversations/handoff", new
            {
                repository,
                base_sha = baseSha,
                base_branch = baseBranch,
                diff_patch = diffPatch,
                message,
                metadata = metadata is null
                    ? (JsonElement?)null
                    : JsonSerializer.SerializeToElement(metadata),
            }, Json, ct);
        await EnsureAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    /// <summary>
    /// SPEC-150 RF-002: GET …/{id}/handoff-package → (status, body).
    /// Sem EnsureAsync — caller trata 403/404/409/422.
    /// </summary>
    public async Task<(int Status, JsonElement Body)> GetHandoffPackageAsync(
        string conversationId, bool regenerate = false,
        CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/api/v1/conversations/{conversationId}"
            + $"/handoff-package{(regenerate ? "?regenerate=1" : "")}", ct);
        var body = await resp.Content
            .ReadFromJsonAsync<JsonElement>(ct);
        return ((int)resp.StatusCode, body);
    }

    /// <summary>
    /// SPEC-150 RF-002: resolve a conversa dona do PR <paramref name="number"/>
    /// — varre o /search (limite) e casa <c>pull_requests[].number</c>.
    /// </summary>
    public async Task<string?> FindConversationByPrAsync(
        int number, CancellationToken ct = default)
    {
        var page = await _http.GetFromJsonAsync<JsonElement>(
            $"{BaseUrl}/api/v1/app-conversations/search?limit=100",
            Json, ct);
        if (!page.TryGetProperty("items", out var items))
        {
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var i)
                ? i.GetString() : null;
            if (id is null)
            {
                continue;
            }

            var conv = await GetConversationAsync(id, ct);
            if (conv is { } c
                && c.TryGetProperty("pull_requests", out var prs))
            {
                foreach (var pr in prs.EnumerateArray())
                {
                    if (pr.TryGetProperty("number", out var n)
                        && n.GetInt32() == number)
                    {
                        return id;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>SPEC-52: abre PR a partir do workspace da conversa.</summary>
    public async Task<JsonElement> CreatePullRequestAsync(
        string conversationId, string title, string body,
        string? baseBranch, bool draft, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"{BaseUrl}/api/v1/app-conversations/{conversationId}/pull-request",
            new
            {
                title,
                body,
                @base = baseBranch,
                draft,
            }, Json, ct);
        await EnsureAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    /// <summary>SPEC-152: POST /evals — dispara run de benchmark (202).</summary>
    public async Task<HttpResponseMessage> CreateEvalAsync(
        string suite, string? model, string? routeOverride, int? n,
        string? tasksPath, bool estimateAcknowledged,
        CancellationToken ct = default) =>
        await _http.PostAsJsonAsync($"{BaseUrl}/api/v1/evals", new
        {
            suite,
            model,
            route_override = routeOverride,
            n,
            tasks_path = tasksPath,
            estimate_acknowledged = estimateAcknowledged,
        }, Json, ct);

    /// <summary>SPEC-152: GET /evals/{id}.</summary>
    public async Task<JsonElement?> GetEvalAsync(
        string runId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/api/v1/evals/{runId}", ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<JsonElement>(ct) : null;
    }

    /// <summary>SPEC-152: GET /evals with suite/status/limit filters.</summary>
    public async Task<JsonElement> ListEvalsAsync(
        string? suite, string? status, int limit = 20,
        CancellationToken ct = default)
    {
        var q = $"?limit={limit}"
            + (suite is null ? "" : $"&suite={Uri.EscapeDataString(suite)}")
            + (status is null
                ? "" : $"&status={Uri.EscapeDataString(status)}");
        return await _http.GetFromJsonAsync<JsonElement>(
            $"{BaseUrl}/api/v1/evals{q}", Json, ct);
    }

    /// <summary>SPEC-152: GET /evals/{id}/results.</summary>
    public async Task<JsonElement> GetEvalResultsAsync(
        string runId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<JsonElement>(
            $"{BaseUrl}/api/v1/evals/{runId}/results", Json, ct);

    /// <summary>SPEC-152: POST /evals/{id}/cancel.</summary>
    public async Task<JsonElement> CancelEvalAsync(
        string runId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync(
            $"{BaseUrl}/api/v1/evals/{runId}/cancel", null, ct);
        await EnsureAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    /// <summary>SPEC-152: POST /evals/estimate.</summary>
    public async Task<JsonElement> EstimateEvalAsync(
        string suite, int? n, string? model, string? tasksPath,
        CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"{BaseUrl}/api/v1/evals/estimate", new
            {
                suite,
                n,
                model,
                tasks_path = tasksPath,
            }, Json, ct);
        await EnsureAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    /// <summary>SPEC-152: GET /evals/scorecard with suite/last filters.</summary>
    public async Task<JsonElement> GetEvalScorecardAsync(
        string? suite, int last = 10, CancellationToken ct = default)
    {
        var q = $"?last={last}"
            + (suite is null ? "" : $"&suite={Uri.EscapeDataString(suite)}");
        return await _http.GetFromJsonAsync<JsonElement>(
            $"{BaseUrl}/api/v1/evals/scorecard{q}", Json, ct);
    }

    private static async Task EnsureAsync(
        HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"HTTP {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}");
    }
}