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

    /// <summary>SPEC-44: lista com filtro "Aguardando você" (?attention=1).</summary>
    public async Task<IReadOnlyList<ConversationItem>>
        ListAttentionConversationsAsync(CancellationToken ct = default)
    {
        var page = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/app-conversations/search?attention=1&limit=100", ct);
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

    /// <summary>SPEC-44: inbox de attention (count + items).</summary>
    public async Task<AttentionListDto> GetAttentionAsync(
        CancellationToken ct = default)
    {
        var resp = await http.GetAsync(
            "/api/v1/app-conversations/attention", ct);
        return resp.IsSuccessStatusCode
            ? (await resp.Content.ReadFromJsonAsync<AttentionListDto>(
                Json, ct)) ?? new AttentionListDto()
            : new AttentionListDto();
    }

    /// <summary>
    /// SPEC-44: dismiss de attention — false quando 409 (confirmation
    /// exige decisão real) ou 404.
    /// </summary>
    public async Task<bool> DismissAttentionAsync(
        string conversationId, CancellationToken ct = default)
    {
        var resp = await http.PostAsync(
            $"/api/v1/app-conversations/{conversationId}/attention/dismiss",
            null, ct);
        return resp.IsSuccessStatusCode;
    }

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

    /// <summary>
    /// SPEC-50: modelos permitidos pelo proxy LiteLLM quando o usuário
    /// tem virtual key BYOR ativa; <c>null</c> = BYOR inativo
    /// (503/not configured) → o catálogo segue sem filtro.
    /// </summary>
    public async Task<IReadOnlyList<string>?> GetByorPermittedModelsAsync(
        CancellationToken ct = default)
    {
        var status = await http.GetAsync("/api/v1/keys/llm/byor", ct);
        if (!status.IsSuccessStatusCode)
        {
            return null;
        }

        var info = await status.Content.ReadFromJsonAsync<JsonElement>(
            Json, ct);
        if (!info.TryGetProperty("configured", out var cfg)
            || !cfg.GetBoolean())
        {
            return null;
        }

        var permitted = await http.GetAsync(
            "/api/v1/keys/llm/byor/permitted", ct);
        if (!permitted.IsSuccessStatusCode)
        {
            return null;
        }

        var doc = await permitted.Content.ReadFromJsonAsync<JsonElement>(
            Json, ct);
        var ids = new List<string>();
        if (doc.TryGetProperty("models", out var arr))
        {
            foreach (var m in arr.EnumerateArray())
            {
                if (m.GetString() is { Length: > 0 } id)
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    /// <summary>SPEC-26/29: POST /{id}/mode — overlay plan|build.</summary>
    public async Task SetModeAsync(
        string conversationId, string mode, CancellationToken ct = default) =>
        (await http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{conversationId}/mode",
            new { mode }, Json, ct)).EnsureSuccessStatusCode();

    /// <summary>SPEC-45 RF-003: POST /{id}/context/compact (/compact).</summary>
    public async Task CompactContextAsync(
        string conversationId, CancellationToken ct = default) =>
        (await http.PostAsync(
            $"/api/v1/app-conversations/{conversationId}/context/compact",
            null, ct)).EnsureSuccessStatusCode();

    /// <summary>SPEC-45 RF-003: GET /{id}/skills (repo+user skills).</summary>
    public async Task<IReadOnlyList<SkillItem>>
        GetConversationSkillsAsync(
            string conversationId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<SkillItem>>(
            $"/api/v1/app-conversations/{conversationId}/skills",
            Json, ct) ?? [];

    /// <summary>
    /// SPEC-45 RF-005: POST /{id}/uploads (multipart `file`) →
    /// <c>{path}</c> no workspace; null em erro de validação (4xx/409).
    /// </summary>
    public async Task<string?> UploadFileAsync(
        string conversationId, string fileName, byte[] content,
        string? contentType = null, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        using var fc = new ByteArrayContent(content);
        fc.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(
                contentType ?? "application/octet-stream");
        form.Add(fc, "file", fileName);
        var resp = await http.PostAsync(
            $"/api/v1/app-conversations/{conversationId}/uploads",
            form, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>(
            Json, ct);
        return dto.TryGetProperty("path", out var p)
            ? p.GetString() : null;
    }

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

    // ---- sharing (SPEC-56) --------------------------------------------

    /// <summary>
    /// POST /app-conversations/{id}/share → link público. O token bruto
    /// vem só na criação (null quando o link já existia).
    /// </summary>
    public sealed record ShareLinkDto(string Url, string? Token,
        DateTimeOffset? ExpiresAt);

    /// <summary>Estado de compartilhamento da conversa.</summary>
    public sealed record ShareStatusDto(
        bool Shared, string? Url, DateTimeOffset? ExpiresAt);

    public async Task<ShareLinkDto?> CreateShareAsync(
        string conversationId, CancellationToken ct = default)
    {
        var resp = await http.PostAsync(
            $"/api/v1/app-conversations/{Uri.EscapeDataString(conversationId)}/share",
            content: null, ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<ShareLinkDto>(Json, ct)
            : null;
    }

    public Task<ShareStatusDto?> GetShareStatusAsync(
        string conversationId, CancellationToken ct = default) =>
        GetOrNull<ShareStatusDto>(
            $"/api/v1/app-conversations/{conversationId}/share", ct);

    public async Task<bool> RevokeShareAsync(
        string conversationId, CancellationToken ct = default)
    {
        var resp = await http.DeleteAsync(
            $"/api/v1/app-conversations/{conversationId}/share", ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>URL do zip de trajetória (download direto).</summary>
    public static string DownloadUrl(string conversationId) =>
        $"/api/v1/app-conversations/{conversationId}/download";

    /// <summary>
    /// GET /shared/{token} — payload público (metadados + 1ª página de
    /// eventos filtrados). Sem auth.
    /// </summary>
    public async Task<SharedConversationDto?> GetSharedConversationAsync(
        string token, CancellationToken ct = default)
    {
        var resp = await http.GetAsync(
            $"/api/v1/shared/{Uri.EscapeDataString(token)}", ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        var items = new List<ClientEvent>();
        if (doc.TryGetProperty("items", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
            {
                items.Add(new ClientEvent { Raw = el.Clone() });
            }
        }

        return new SharedConversationDto(
            Title: doc.TryGetProperty("title", out var t)
                ? t.GetString() ?? "" : "",
            SelectedRepository: doc.TryGetProperty(
                "selected_repository", out var r) ? r.GetString() : null,
            Items: items,
            NextPageId: doc.TryGetProperty("next_page_id", out var n)
                && n.ValueKind == JsonValueKind.String
                    ? n.GetString() : null);
    }

    /// <summary>Próxima página de eventos públicos.</summary>
    public async Task<(IReadOnlyList<ClientEvent> Items, string? Next)>
        GetSharedEventsAsync(
            string token, string pageId, CancellationToken ct = default)
    {
        var resp = await http.GetAsync(
            $"/api/v1/shared/{Uri.EscapeDataString(token)}/events"
            + $"?page_id={Uri.EscapeDataString(pageId)}", ct);
        var items = new List<ClientEvent>();
        string? next = null;
        if (resp.IsSuccessStatusCode)
        {
            var doc = await resp.Content
                .ReadFromJsonAsync<JsonElement>(Json, ct);
            if (doc.TryGetProperty("items", out var arr))
            {
                foreach (var el in arr.EnumerateArray())
                {
                    items.Add(new ClientEvent { Raw = el.Clone() });
                }
            }

            if (doc.TryGetProperty("next_page_id", out var n)
                && n.ValueKind == JsonValueKind.String)
            {
                next = n.GetString();
            }
        }

        return (items, next);
    }

    /// <summary>Payload da página pública de conversa compartilhada.</summary>
    public sealed record SharedConversationDto(
        string Title, string? SelectedRepository,
        IReadOnlyList<ClientEvent> Items, string? NextPageId);

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
            && doc.TryGetProperty("content", out var _))
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
                Reverted = false,
                Conflicts = [],
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
        var resp = await http.GetAsync(new Uri(url, UriKind.Relative), ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<T>(Json, ct) : default;
    }

    // ---- automations (SPEC-47) ------------------------------------------

    /// <summary>Lista as automations do usuário corrente.</summary>
    public async Task<IReadOnlyList<AutomationItem>> ListAutomationsAsync(
        CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<AutomationItem>>(
            "/api/v1/automations", Json, ct) ?? [];

    /// <summary>Detalhe de uma automation (null se 404).</summary>
    public Task<AutomationItem?> GetAutomationAsync(
        Guid id, CancellationToken ct = default) =>
        GetOrNull<AutomationItem>($"/api/v1/automations/{id}", ct);

    /// <summary>Cria automation; erro de validação → exception com detalhe.</summary>
    public async Task<AutomationItem> CreateAutomationAsync(
        object body, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync(
            "/api/v1/automations", body, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                await resp.Content.ReadAsStringAsync(ct));
        }
        return (await resp.Content
            .ReadFromJsonAsync<AutomationItem>(Json, ct))!;
    }

    /// <summary>PATCH parcial (ex.: {enabled=false}).</summary>
    public async Task<AutomationItem?> PatchAutomationAsync(
        Guid id, object patch, CancellationToken ct = default)
    {
        var resp = await http.PatchAsJsonAsync(
            $"/api/v1/automations/{id}", patch, Json, ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<AutomationItem>(Json, ct)
            : null;
    }

    /// <summary>DELETE → true se removeu.</summary>
    public async Task<bool> DeleteAutomationAsync(
        Guid id, CancellationToken ct = default) =>
        (await http.DeleteAsync($"/api/v1/automations/{id}", ct))
            .IsSuccessStatusCode;

    /// <summary>Dispara imediato; 409 → null (já running).</summary>
    public async Task<AutomationRunItem?> RunAutomationNowAsync(
        Guid id, CancellationToken ct = default)
    {
        var resp = await http.PostAsync(
            $"/api/v1/automations/{id}/run-now", null, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return null;
        }
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return new AutomationRunItem(
            doc.GetProperty("run_id").GetGuid(), id,
            DateTimeOffset.UtcNow, null,
            doc.GetProperty("status").GetString() ?? "running",
            doc.TryGetProperty("conversation_id", out var c)
            && c.ValueKind == JsonValueKind.String
                ? c.GetGuid() : null, null);
    }

    /// <summary>Histórico de runs de uma automation.</summary>
    public async Task<IReadOnlyList<AutomationRunItem>> ListAutomationRunsAsync(
        Guid id, int take = 50, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<AutomationRunItem>>(
            $"/api/v1/automations/{id}/runs?take={take}", Json, ct) ?? [];

    /// <summary>Cria webhook hook → token exibido uma única vez.</summary>
    public async Task<string?> CreateAutomationHookAsync(
        Guid id, CancellationToken ct = default)
    {
        var resp = await http.PostAsync(
            $"/api/v1/automations/{id}/hooks", null, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return doc.GetProperty("token").GetString();
    }


    // ---- SPEC-63: knowledge pipeline ---------------------------------

    public async Task<IReadOnlyList<KnowledgeSuggestionItem>>
        ListKnowledgeSuggestionsAsync(string? status = null,
            CancellationToken ct = default) =>
        (await GetOrNull<List<KnowledgeSuggestionItem>>(
            $"/api/v1/knowledge/suggestions{(status is null ? "" : $"?status={status}")}",
            ct)) ?? [];

    public async Task ApproveSuggestionAsync(
        Guid id, string? contentOverride, CancellationToken ct = default)
        => await http.PostAsJsonAsync(
            $"/api/v1/knowledge/suggestions/{id}/approve",
            new { contentOverride }, Json, ct);

    public async Task DismissSuggestionAsync(
        Guid id, string? reason, CancellationToken ct = default)
        => await http.PostAsJsonAsync(
            $"/api/v1/knowledge/suggestions/{id}/dismiss",
            new { reason }, Json, ct);

    public async Task GroomKnowledgeAsync(CancellationToken ct = default)
        => await http.PostAsync("/api/v1/knowledge/groom", null, ct);
}

/// <summary>SPEC-63: sugestão de knowledge na fila.</summary>
public sealed record KnowledgeSuggestionItem(
    Guid Id, string Scope, string? RepoPath, string Title,
    string Content, string Kind, string Status, string? Reason,
    string? Resolution, DateTimeOffset CreatedAt);