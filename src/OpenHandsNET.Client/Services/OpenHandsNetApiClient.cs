using System.Net.Http.Json;
using System.Text.Json;
using OpenHandsNET.Application.Checkpoints;

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

    /// <summary>SPEC-26: GET /models → ids disponíveis + default.</summary>
    public async Task<(IReadOnlyList<string> Ids, string? Default)>
        GetModelsAsync(CancellationToken ct = default)
    {
        var doc = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/models", Json, ct);
        var ids = new List<string>();
        if (doc.TryGetProperty("models", out var arr))
        {
            foreach (var m in arr.EnumerateArray())
            {
                if (m.TryGetProperty("id", out var id))
                {
                    ids.Add(id.GetString() ?? "");
                }
            }
        }

        var def = doc.TryGetProperty("default", out var d)
            ? d.GetString() : null;
        return (ids, def);
    }

    /// <summary>SPEC-26: POST /{id}/switch_acp_model — próximo run.</summary>
    public async Task SwitchModelAsync(
        string conversationId, string model, CancellationToken ct = default) =>
        (await http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{conversationId}/switch_acp_model",
            new { model }, Json, ct)).EnsureSuccessStatusCode();

    /// <summary>SPEC-26/29: POST /{id}/mode — overlay plan|build.</summary>
    public async Task SetModeAsync(
        string conversationId, string mode, CancellationToken ct = default) =>
        (await http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{conversationId}/mode",
            new { mode }, Json, ct)).EnsureSuccessStatusCode();

    /// <summary>
    /// SPEC-40 RF-002: GET /{id}/surfaces — surfaces do sandbox com URL
    /// pública + preview token (null quando routing internal/desligada).
    /// </summary>
    public async Task<IReadOnlyList<SurfaceItem>> GetSurfacesAsync(
        string conversationId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<SurfaceItem>>(
            $"/api/v1/app-conversations/{conversationId}/surfaces",
            Json, ct) ?? [];

    /// <summary>Surface publicada pelo sandbox.</summary>
    public sealed record SurfaceItem(
        string Name, string Protocol, int Port,
        string? Url, DateTimeOffset? ExpiresAt);

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

    /// <summary>
    /// SPEC-42 RF-001: leitura com ETag (sha256 do conteúdo) — o editor
    /// guarda o etag para o optimistic lock do PUT.
    /// </summary>
    public async Task<FileReadResult?> ReadFileWithEtagAsync(
        string conversationId, string path, CancellationToken ct = default)
    {
        var resp = await http.GetAsync(
            $"/api/v1/app-conversations/{conversationId}/file"
            + $"?path={Uri.EscapeDataString(path)}", ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var etag = doc.TryGetProperty("etag", out var e)
            ? e.GetString() : null;
        if (doc.TryGetProperty("encoding", out var enc)
            && enc.GetString() == "base64")
        {
            return new FileReadResult(
                doc.GetProperty("content").GetString() ?? "", etag,
                IsBinary: true);
        }

        return new FileReadResult(
            doc.TryGetProperty("content", out var c)
                ? c.GetString() ?? "" : "", etag, IsBinary: false);
    }

    /// <summary>
    /// SPEC-42 RF-001: PUT <c>{content_base64, etag?}</c> — 409 devolve
    /// <see cref="FileWriteResult.Conflict"/> com o etag corrente.
    /// </summary>
    public async Task<FileWriteResult> WriteFileAsync(
        string conversationId, string path, string content,
        string? etag, CancellationToken ct = default)
    {
        var resp = await http.PutAsJsonAsync(
            $"/api/v1/app-conversations/{conversationId}/file"
            + $"?path={Uri.EscapeDataString(path)}",
            new
            {
                content_base64 = Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes(content)),
                etag,
            }, Json, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return new FileWriteResult(false, null, Conflict: true);
        }

        if (!resp.IsSuccessStatusCode)
        {
            return new FileWriteResult(false, null, Conflict: false);
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new FileWriteResult(true,
            doc.TryGetProperty("etag", out var e) ? e.GetString() : null,
            Conflict: false);
    }

    /// <summary>SPEC-42 RF-001: mkdir — comando fixo server-side.</summary>
    public Task<bool> MkdirAsync(string conversationId, string path,
        CancellationToken ct = default) =>
        FileOpAsync(conversationId, "mkdir", new { path }, ct);

    /// <summary>SPEC-42 RF-001: rename/move.</summary>
    public Task<bool> RenameAsync(string conversationId, string from,
        string to, CancellationToken ct = default) =>
        FileOpAsync(conversationId, "rename", new { from, to }, ct);

    /// <summary>SPEC-42 RF-001: delete (rm -rf guardado).</summary>
    public Task<bool> DeleteFileAsync(string conversationId, string path,
        CancellationToken ct = default) =>
        FileOpAsync(conversationId, "delete", new { path }, ct);

    private async Task<bool> FileOpAsync(
        string conversationId, string op, object body,
        CancellationToken ct)
    {
        var resp = await http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{conversationId}/files/{op}",
            body, Json, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>SPEC-42 RF-001: busca global (grep -rInF, cap 500).</summary>
    public async Task<IReadOnlyList<FileSearchMatch>> SearchFilesAsync(
        string conversationId, string q, string? glob = null,
        CancellationToken ct = default)
    {
        var url = $"/api/v1/app-conversations/{conversationId}/files/search"
            + $"?q={Uri.EscapeDataString(q)}"
            + (glob is { Length: > 0 }
                ? $"&glob={Uri.EscapeDataString(glob)}" : "");
        var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return [];
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return doc.TryGetProperty("matches", out var m)
            ? m.Deserialize<List<FileSearchMatch>>(Json) ?? [] : [];
    }

    /// <summary>SPEC-42 RF-005: commit → <c>{sha}</c> ou null.</summary>
    public async Task<string?> GitCommitAsync(
        string conversationId, string message,
        IReadOnlyList<string>? files = null, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{conversationId}/git/commit",
            new { message, files }, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return doc.TryGetProperty("sha", out var s) ? s.GetString() : null;
    }

    /// <summary>
    /// SPEC-42 RF-008: exec one-shot — usado pelo painel de testes do IDE.
    /// </summary>
    public async Task<ExecResult> ExecOneShotAsync(
        string conversationId, string command, int timeoutS = 300,
        CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{conversationId}/exec",
            new { command, timeout_s = timeoutS }, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return new ExecResult(-1, "",
                $"exec failed ({(int)resp.StatusCode})", false);
        }

        return (await resp.Content.ReadFromJsonAsync<ExecResult>(Json, ct))
            ?? new ExecResult(-1, "", "empty response", false);
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

    // ---- checkpoints (SPEC-43) ------------------------------------------

    /// <summary>SPEC-43: lista de checkpoints (mais novo primeiro).</summary>
    public async Task<IReadOnlyList<CheckpointItem>> ListCheckpointsAsync(
        string conversationId, CancellationToken ct = default)
    {
        var resp = await http.GetAsync(
            $"/api/v1/app-conversations/{conversationId}/checkpoints", ct);
        if (!resp.IsSuccessStatusCode)
        {
            return [];
        }

        return (await resp.Content.ReadFromJsonAsync<
            List<CheckpointItem>>(Json, ct)) ?? [];
    }

    /// <summary>SPEC-43: detalhe/diffstat de um checkpoint.</summary>
    public async Task<CheckpointDetail?> GetCheckpointAsync(
        string conversationId, string hash, CancellationToken ct = default)
    {
        var resp = await http.GetAsync(
            $"/api/v1/app-conversations/{conversationId}/checkpoints/{hash}",
            ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<CheckpointDetail>(Json, ct)
            : null;
    }

    /// <summary>SPEC-43: snapshot manual.</summary>
    public async Task<CheckpointItem?> CreateCheckpointAsync(
        string conversationId, string label,
        CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{conversationId}/checkpoints",
            new { label }, Json, ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<CheckpointItem>(Json, ct)
            : null;
    }

    /// <summary>
    /// SPEC-43: revert — <c>409</c> devolve os conflitos de drift com
    /// <c>Reverted=false</c>.
    /// </summary>
    public async Task<CheckpointRevertResult> RevertCheckpointAsync(
        string conversationId, string hash, bool force = false,
        CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{conversationId}/checkpoints" +
            $"/{hash}/revert", new { force }, Json, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var doc = await resp.Content
                .ReadFromJsonAsync<JsonElement>(Json, ct);
            return new CheckpointRevertResult
            {
                Reverted = false,
                Conflicts = StrList(doc, "conflicts"),
                Untracked = StrList(doc, "untracked"),
            };
        }

        return resp.IsSuccessStatusCode
            ? (await resp.Content.ReadFromJsonAsync<
                CheckpointRevertResult>(Json, ct))!
            : new CheckpointRevertResult
            {
                Reverted = false, Conflicts = [],
                Untracked = [$"http {(int)resp.StatusCode}"],
            };
    }

    private static List<string> StrList(JsonElement doc, string name) =>
        doc.TryGetProperty(name, out var a)
            && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
            : [];

    /// <summary>
    /// SPEC-21 RF-006: conteúdo do arquivo no <paramref name="gitRef"/>
    /// (default HEAD) — lado "original" do Monaco diff editor. Null quando
    /// o arquivo não existe naquela revisão.
    /// </summary>
    public async Task<string?> GetGitFileAtRefAsync(
        string conversationId, string path, string gitRef = "HEAD",
        CancellationToken ct = default)
    {
        var resp = await http.GetAsync(
            $"/api/v1/app-conversations/{conversationId}/git/file"
            + $"?path={Uri.EscapeDataString(path)}"
            + $"&ref={Uri.EscapeDataString(gitRef)}", ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return doc.TryGetProperty("content", out var c) ? c.GetString() : null;
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

    // ---- user preferences (SPEC-26) -------------------------------------

    /// <summary>GET /users/me/preferences → dict solto ({} se vazio/401).</summary>
    public async Task<Dictionary<string, string>> GetPreferencesAsync(
        CancellationToken ct = default)
    {
        var resp = await http.GetAsync("/api/v1/users/me/preferences", ct);
        if (!resp.IsSuccessStatusCode)
        {
            return new Dictionary<string, string>();
        }

        return await resp.Content
            .ReadFromJsonAsync<Dictionary<string, string>>(Json, ct)
            ?? new Dictionary<string, string>();
    }

    /// <summary>PUT /users/me/preferences — merge raso no servidor.</summary>
    public async Task PutPreferencesAsync(
        IReadOnlyDictionary<string, string> prefs, CancellationToken ct = default)
    {
        var resp = await http.PutAsJsonAsync(
            "/api/v1/users/me/preferences", prefs, ct);
        resp.EnsureSuccessStatusCode();
    }

    private async Task<T?> GetOrNull<T>(string url, CancellationToken ct)
    {
        var resp = await http.GetAsync(url, ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<T>(Json, ct) : default;
    }
}
