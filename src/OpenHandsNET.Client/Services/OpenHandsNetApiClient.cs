using System.Net.Http.Json;
using System.Text.Json;

namespace OpenHandsNET.Client.Services;

/// <summary>
/// Typed client da REST API V1 (<c>/api/v1</c>). Único ponto de acesso
/// HTTP da UI — componentes nunca chamam <see cref="HttpClient"/> direto
/// (guardrail SPEC-05).
/// </summary>
public sealed class OpenHandsNetApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    // ---- conversations ------------------------------------------------

    /// <summary>POST /app-conversations → start-task (async).</summary>
    public async Task<StartTaskItem> CreateConversationAsync(
        string? initialMessage, string? selectedRepository,
        bool run = true, bool confirmationMode = false,
        CancellationToken ct = default)
    {
        object? initial = initialMessage is null ? null : new
        {
            role = "user",
            content = new[] { new { type = "text", text = initialMessage } },
            run,
        };
        var resp = await http.PostAsJsonAsync("/api/v1/app-conversations", new
        {
            initial_message = initial,
            selected_repository = selectedRepository,
            confirmation_mode = confirmationMode,
        }, Json, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<StartTaskItem>(Json, ct))!;
    }

    public async Task<StartTaskItem?> GetStartTaskAsync(
        string taskId, CancellationToken ct = default)
    {
        var resp = await http.GetAsync(
            $"/api/v1/app-conversations/start-tasks/{taskId}", ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<StartTaskItem>(Json, ct)
            : null;
    }

    public async Task<IReadOnlyList<ConversationItem>> ListConversationsAsync(
        CancellationToken ct = default)
    {
        var page = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/app-conversations/search?limit=100", ct);
        var items = new List<ConversationItem>();
        if (page.TryGetProperty("items", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
            {
                items.Add(el.Deserialize<ConversationItem>(Json)!);
            }
        }

        return items;
    }

    public Task<ConversationItem?> GetConversationAsync(
        string id, CancellationToken ct = default) =>
        GetOrNull<ConversationItem>($"/api/v1/app-conversations/{id}", ct);

    public async Task SendMessageAsync(
        string conversationId, string text, bool run = true,
        CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{conversationId}/send-message", new
            {
                role = "user",
                content = new[] { new { type = "text", text } },
                run,
            }, Json, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteConversationAsync(
        string id, CancellationToken ct = default) =>
        (await http.DeleteAsync($"/api/v1/app-conversations/{id}", ct))
            .EnsureSuccessStatusCode();

    // ---- events --------------------------------------------------------

    /// <summary>GET /conversation/{id}/events/search → eventos crus.</summary>
    public async Task<IReadOnlyList<ClientEvent>> GetEventsAsync(
        string conversationId, string? pageId = null, int limit = 100,
        CancellationToken ct = default)
    {
        var url = $"/api/v1/conversation/{conversationId}/events/search?limit={limit}"
            + (pageId is null ? "" : $"&page_id={Uri.EscapeDataString(pageId)}");
        var page = await http.GetFromJsonAsync<JsonElement>(url, ct);
        var items = new List<ClientEvent>();
        if (page.TryGetProperty("items", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
            {
                items.Add(new ClientEvent { Raw = el.Clone() });
            }
        }

        return items;
    }

    // ---- workspace / git proxies --------------------------------------

    public async Task<IReadOnlyList<FileItem>> ListFilesAsync(
        string conversationId, string path = "/workspace",
        CancellationToken ct = default)
    {
        var list = await http.GetFromJsonAsync<List<FileItem>>(
            $"/api/v1/app-conversations/{conversationId}/files"
            + $"?path={Uri.EscapeDataString(path)}", Json, ct);
        return list ?? [];
    }

    public async Task<string> ReadFileAsync(
        string conversationId, string path, CancellationToken ct = default)
    {
        var resp = await http.GetAsync(
            $"/api/v1/app-conversations/{conversationId}/file"
            + $"?path={Uri.EscapeDataString(path)}", ct);
        if (!resp.IsSuccessStatusCode)
        {
            return $"# unable to read {path} ({(int)resp.StatusCode})";
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (doc.TryGetProperty("encoding", out var enc)
            && enc.GetString() == "base64"
            && doc.TryGetProperty("content", out var b64))
        {
            return "[binary file — base64 omitted]";
        }

        return doc.TryGetProperty("content", out var c)
            ? c.GetString() ?? "" : "";
    }

    public async Task<JsonElement> GetGitChangesAsync(
        string conversationId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/app-conversations/{conversationId}/git/changes", ct);

    public async Task<string> GetGitDiffAsync(
        string conversationId, CancellationToken ct = default)
    {
        var resp = await http.GetAsync(
            $"/api/v1/app-conversations/{conversationId}/git/diff?format=raw", ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadAsStringAsync(ct) : "";
    }

    public async Task<IReadOnlyList<JsonElement>> GetGitLogAsync(
        string conversationId, CancellationToken ct = default)
    {
        var list = await http.GetFromJsonAsync<List<JsonElement>>(
            $"/api/v1/app-conversations/{conversationId}/git/log?limit=100", ct);
        return list ?? [];
    }

    // ---- settings / secrets -------------------------------------------

    public async Task<JsonElement> GetSettingsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<JsonElement>("/api/v1/settings", ct);

    public async Task SaveSettingsAsync(
        JsonElement settings, CancellationToken ct = default) =>
        (await http.PostAsJsonAsync("/api/v1/settings", settings, ct))
            .EnsureSuccessStatusCode();

    public async Task<IReadOnlyList<string>> ListSecretsAsync(
        CancellationToken ct = default)
    {
        var list = await http.GetFromJsonAsync<List<JsonElement>>(
            "/api/v1/secrets", ct);
        return list?
            .Select(e => e.TryGetProperty("name", out var n)
                ? n.GetString() ?? "" : "")
            .Where(s => s.Length > 0).ToList() ?? [];
    }

    public async Task PutSecretAsync(
        string name, string value, CancellationToken ct = default) =>
        (await http.PutAsJsonAsync(
            $"/api/v1/secrets/{Uri.EscapeDataString(name)}",
            new { value }, ct)).EnsureSuccessStatusCode();

    public async Task DeleteSecretAsync(
        string name, CancellationToken ct = default) =>
        (await http.DeleteAsync(
            $"/api/v1/secrets/{Uri.EscapeDataString(name)}", ct))
            .EnsureSuccessStatusCode();

    // ---- mcp (SPEC-07) ----------------------------------------------------

    public Task<IReadOnlyList<JsonElement>> ListMcpServersAsync(
        CancellationToken ct = default) => GetListAsync("/api/v1/mcp/servers", ct);

    public async Task SaveMcpServerAsync(
        object config, CancellationToken ct = default) =>
        (await http.PostAsJsonAsync("/api/v1/mcp/servers", config, Json, ct))
            .EnsureSuccessStatusCode();

    public async Task DeleteMcpServerAsync(
        string name, CancellationToken ct = default) =>
        (await http.DeleteAsync(
            $"/api/v1/mcp/servers/{Uri.EscapeDataString(name)}", ct))
            .EnsureSuccessStatusCode();

    public async Task<JsonElement> ProbeMcpServerAsync(
        object config, CancellationToken ct = default) =>
        await (await http.PostAsJsonAsync(
            "/api/v1/mcp/test", new { server = config }, Json, ct))
            .Content.ReadFromJsonAsync<JsonElement>(Json, ct);

    // ---- config ---------------------------------------------------------

    private async Task<IReadOnlyList<JsonElement>> GetListAsync(
        string url, CancellationToken ct) =>
        await http.GetFromJsonAsync<List<JsonElement>>(url, Json, ct) ?? [];

    public Task<JsonElement> GetConfigAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<JsonElement>("/api/v1/config", ct)!;

    private async Task<T?> GetOrNull<T>(string url, CancellationToken ct)
    {
        var resp = await http.GetAsync(url, ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<T>(Json, ct) : default;
    }
}
