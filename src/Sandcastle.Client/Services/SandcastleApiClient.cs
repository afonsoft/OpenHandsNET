using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Sandcastle.Application.Checkpoints;

namespace Sandcastle.Client.Services;

/// <summary>
/// Typed client da REST API V1 (<c>/api/v1</c>). Único ponto de acesso
/// HTTP da UI — componentes nunca chamam <see cref="HttpClient"/> direto
/// (guardrail SPEC-05).
/// </summary>
public sealed class SandcastleApiClient(HttpClient http)
{
    private const string Content = "content";
    private const string ItemsProp = "items";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    // ---- conversations ------------------------------------------------

    /// <summary>
    /// POST /app-conversations → start-task (async). SPEC-146:
    /// <paramref name="repositories"/> ordenado (índice 0 = primário,
    /// max 4) → <c>selected_repositories</c>; o legado
    /// <c>selected_repository</c> continua espelhando o primário.
    /// </summary>
    public async Task<StartTaskItem> CreateConversationAsync(
        string? initialMessage, string? selectedRepository,
        bool run = true, bool confirmationMode = false,
        IReadOnlyList<string>? repositories = null,
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
            selected_repositories = repositories,
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
        if (page.TryGetProperty(ItemsProp, out var arr))
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
        if (page.TryGetProperty(ItemsProp, out var arr))
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

    /// <summary>
    /// SPEC-135 RF-001: POST .../fork → id da conversa-filha.
    /// <paramref name="atEventId"/> opcional (corte).
    /// </summary>
    public async Task<ForkConversationResult> ForkConversationAsync(
        string conversationId, string? atEventId = null,
        string? title = null, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{Uri.EscapeDataString(conversationId)}/fork",
            new { at_event_id = atEventId, title }, Json, ct);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return new ForkConversationResult(
            doc.GetProperty("id").GetString()!,
            doc.GetProperty("start_task_id").GetString()!);
    }

    /// <summary>
    /// SPEC-135 RF-003: POST .../undo → resultado; null quando 404
    /// <c>no_turn</c> (idempotente). 409 (run ativa) propaga.
    /// </summary>
    public async Task<UndoResult?> UndoConversationAsync(
        string conversationId, CancellationToken ct = default)
    {
        var resp = await http.PostAsync(
            $"/api/v1/app-conversations/{Uri.EscapeDataString(conversationId)}/undo", null, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        var files = new List<string>();
        if (doc.TryGetProperty("files_reverted", out var f))
        {
            files.AddRange(f.EnumerateArray()
                .Select(e => e.GetString() ?? ""));
        }

        return new UndoResult(
            doc.GetProperty("truncated_events").GetInt32(), files);
    }

    /// <summary>
    /// SPEC-135 RF-005: POST .../files/revert-hunks → aplicados/conflitos
    /// (409 entrega conflicts; nunca lança por conflito de hunk).
    /// </summary>
    public async Task<RevertHunksResult> RevertHunksAsync(
        string conversationId, string path,
        IReadOnlyList<int> hunkIndexes, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{Uri.EscapeDataString(conversationId)}/files/revert-hunks",
            new { path, hunk_indexes = hunkIndexes }, Json, ct);
        if (resp.IsSuccessStatusCode)
        {
            var ok = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            return new RevertHunksResult(
                ok.GetProperty("applied").EnumerateArray()
                    .Select(e => e.GetInt32()).ToList(), []);
        }

        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var bad = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            return new RevertHunksResult(
                bad.GetProperty("applied").EnumerateArray()
                    .Select(e => e.GetInt32()).ToList(),
                bad.GetProperty("conflicts").EnumerateArray()
                    .Select(e => e.GetInt32()).ToList());
        }

        resp.EnsureSuccessStatusCode();
        return new RevertHunksResult([], []);
    }

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
        if (doc.TryGetProperty(ItemsProp, out var arr))
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
            if (doc.TryGetProperty(ItemsProp, out var arr))
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
        if (page.TryGetProperty(ItemsProp, out var arr))
        {
            foreach (var el in arr.EnumerateArray())
            {
                items.Add(new ClientEvent { Raw = el.Clone() });
            }
        }

        return items;
    }

    /// <summary>SPEC-76 RF-004: sub-conversas delegadas da conversa.</summary>
    public async Task<IReadOnlyList<DelegationItem>> GetDelegationsAsync(
        string conversationId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<DelegationItem>>(
            $"/api/v1/app-conversations/{conversationId}/delegations",
            Json, ct) ?? [];

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
            && doc.TryGetProperty(Content, out var _))
        {
            return "[binary file — base64 omitted]";
        }

        return doc.TryGetProperty(Content, out var c)
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
                doc.GetProperty(Content).GetString() ?? "", etag,
                IsBinary: true);
        }

        return new FileReadResult(
            doc.TryGetProperty(Content, out var c)
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
        return doc.TryGetProperty(Content, out var c) ? c.GetString() : null;
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

    // ---- BYOK user LLM keys (SPEC-134) ------------------------------------

    public sealed record UserLlmKeyInfoDto(
        string Provider, string? MaskedKey, string? BaseUrl,
        string? UpdatedAt);

    public async Task<IReadOnlyList<UserLlmKeyInfoDto>> ListLlmKeysAsync(
        CancellationToken ct = default)
    {
        var list = await http.GetFromJsonAsync<List<JsonElement>>(
            "/api/v1/keys/llm", Json, ct);
        return list?.Select(e => new UserLlmKeyInfoDto(
                e.TryGetProperty("provider", out var p) ? p.GetString() ?? "" : "",
                e.TryGetProperty("masked_key", out var m) ? m.GetString() : null,
                e.TryGetProperty("base_url", out var b) ? b.GetString() : null,
                e.TryGetProperty("updated_at", out var u) ? u.GetString() : null))
            .ToList() ?? [];
    }

    public async Task<IReadOnlyList<JsonElement>> ListLlmKeyProvidersAsync(
        CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<JsonElement>>(
            "/api/v1/keys/llm/providers", Json, ct) ?? [];

    public async Task<UserLlmKeyInfoDto?> PutLlmKeyAsync(
        string provider, string key, string? baseUrl,
        CancellationToken ct = default)
    {
        var resp = await http.PutAsJsonAsync(
            $"/api/v1/keys/llm/{Uri.EscapeDataString(provider)}",
            new { key, base_url = baseUrl }, Json, ct);
        resp.EnsureSuccessStatusCode();
        var e = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return new UserLlmKeyInfoDto(
            e.TryGetProperty("provider", out var p) ? p.GetString() ?? "" : "",
            e.TryGetProperty("masked_key", out var m) ? m.GetString() : null,
            e.TryGetProperty("base_url", out var b) ? b.GetString() : null,
            e.TryGetProperty("updated_at", out var u) ? u.GetString() : null);
    }

    public async Task DeleteLlmKeyAsync(
        string provider, CancellationToken ct = default) =>
        (await http.DeleteAsync(
            $"/api/v1/keys/llm/{Uri.EscapeDataString(provider)}", ct))
            .EnsureSuccessStatusCode();

    public async Task<(bool Ok, string? Detail)> TestLlmKeyAsync(
        string provider, CancellationToken ct = default)
    {
        var resp = await http.PostAsync(
            $"/api/v1/keys/llm/{Uri.EscapeDataString(provider)}/test",
            null, ct);
        var e = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        var ok = e.TryGetProperty("ok", out var o) && o.GetBoolean();
        var detail = e.TryGetProperty("detail", out var d)
            ? d.GetString() : null;
        return (ok, detail);
    }

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

    // ---- marketplace (SPEC-148) ---------------------------------------

    /// <summary>GET /marketplace/entries — catálogo com installs por escopo.</summary>
    public async Task<IReadOnlyList<MarketplaceEntryDto>>
        ListMarketplaceEntriesAsync(string? q = null, string? kind = null,
            CancellationToken ct = default)
    {
        var url = "/api/v1/marketplace/entries?x=1"
            + (q is { } s ? $"&q={Uri.EscapeDataString(s)}" : "")
            + (kind is { } k ? $"&kind={Uri.EscapeDataString(k)}" : "");
        var doc = await http.GetFromJsonAsync<JsonElement>(url, Json, ct);
        return doc.TryGetProperty(ItemsProp, out var items)
            ? items.EnumerateArray()
                .Select(e => e.Deserialize<MarketplaceEntryDto>(Json)!)
                .ToList()
            : [];
    }

    /// <summary>GET /marketplace/entries/{id} — detalhe (readme/files).</summary>
    public Task<MarketplaceEntryDto?> GetMarketplaceEntryAsync(
        string id, CancellationToken ct = default) =>
        http.GetFromJsonAsync<MarketplaceEntryDto>(
            $"/api/v1/marketplace/entries/{Uri.EscapeDataString(id)}",
            Json, ct);

    /// <summary>GET /marketplace/installed — installs visíveis ao caller.</summary>
    public async Task<IReadOnlyList<InstalledSkillDto>>
        ListMarketplaceInstalledAsync(string? scope = null,
            CancellationToken ct = default)
    {
        var url = "/api/v1/marketplace/installed"
            + (scope is { } s ? $"?scope={s}" : "");
        var doc = await http.GetFromJsonAsync<JsonElement>(url, Json, ct);
        return doc.TryGetProperty(ItemsProp, out var items)
            ? items.EnumerateArray()
                .Select(e => e.Deserialize<InstalledSkillDto>(Json)!)
                .ToList()
            : [];
    }

    /// <summary>
    /// POST /marketplace/install — retorna (status, detail) p/ a UI
    /// distinguir 409/422/502 sem exception parsing.
    /// </summary>
    public async Task<(int Status, InstalledSkillDto? Row, string? Detail)>
        InstallMarketplaceAsync(string entryId, string scope,
            string? scopeId = null, string? version = null,
            bool acceptPermissions = false, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("/api/v1/marketplace/install",
            new
            {
                entry_id = entryId,
                version,
                scope,
                scope_id = scopeId,
                accept_permissions = acceptPermissions,
            }, Json, ct);
        if ((int)resp.StatusCode is >= 200 and < 300)
        {
            var row = await resp.Content
                .ReadFromJsonAsync<InstalledSkillDto>(Json, ct);
            return ((int)resp.StatusCode, row, null);
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        return ((int)resp.StatusCode, null, body);
    }

    /// <summary>PATCH /marketplace/installed/{id} — enable/disable.</summary>
    public async Task<bool> SetMarketplaceEnabledAsync(
        Guid id, bool enabled, CancellationToken ct = default) =>
        (await http.PatchAsJsonAsync(
            $"/api/v1/marketplace/installed/{id}",
            new { enabled }, Json, ct)).IsSuccessStatusCode;

    /// <summary>DELETE /marketplace/installed/{id} — remove + dematerializa.</summary>
    public async Task<bool> RemoveMarketplaceAsync(
        Guid id, CancellationToken ct = default) =>
        (await http.DeleteAsync(
            $"/api/v1/marketplace/installed/{id}", ct)).IsSuccessStatusCode;

    /// <summary>POST /marketplace/upgrade/{id} → (status, row|null).</summary>
    public async Task<(int Status, InstalledSkillDto? Row)>
        UpgradeMarketplaceAsync(Guid id, CancellationToken ct = default)
    {
        var resp = await http.PostAsync(
            $"/api/v1/marketplace/upgrade/{id}", null, ct);
        var row = resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<InstalledSkillDto>(Json, ct)
            : (InstalledSkillDto?)null;
        return ((int)resp.StatusCode, row);
    }

    /// <summary>Registry entry DTO (catalog item).</summary>
    public sealed record MarketplaceEntryDto(
        string Id, string Name, string Version, string Kind,
        string? Description, string? Author, IReadOnlyList<string>? Tags,
        string? MinAppVersion, string? Source,
        IReadOnlyList<string>? PermissionsRequired, string? ChangelogUrl,
        string? Readme, IReadOnlyList<string>? EntryIds,
        IReadOnlyList<MarketplaceScopeInstallDto>? Installed,
        bool UpdateAvailable);

    /// <summary>Install aninhado no DTO de entry.</summary>
    public sealed record MarketplaceScopeInstallDto(
        Guid InstalledId, string Scope, string? ScopeId,
        string Version, bool Enabled);

    /// <summary>installed_skills row DTO.</summary>
    public sealed record InstalledSkillDto(
        Guid Id, string EntryId, string Scope, string? ScopeId,
        string Version, bool Enabled, string? InstalledBy,
        DateTimeOffset InstalledAt, bool UpdateAvailable);

    /// <summary>
    /// GET /organizations — orgs do caller com a role dele. Usado pela
    /// UI do marketplace para oferecer o scope <c>org</c> só onde o
    /// papel tem <c>marketplace.manage</c> (owner|admin).
    /// </summary>
    public async Task<IReadOnlyList<OrgSummaryDto>> ListMyOrgsAsync(
        CancellationToken ct = default)
    {
        try
        {
            var doc = await http.GetFromJsonAsync<JsonElement>(
                "/api/v1/organizations", Json, ct);
            return doc.ValueKind == JsonValueKind.Array
                ? doc.EnumerateArray()
                    .Select(e => e.Deserialize<OrgSummaryDto>(Json)!)
                    .ToList()
                : [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    /// <summary>Org do caller (role incluída p/ gating de UI).</summary>
    public sealed record OrgSummaryDto(
        Guid Id, string? Slug, string? Name, string? Role);

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

    // ---- SPEC-147: usage budget + indexing status -------------------------

    /// <summary>GET /usage/budget — budget do caller (null se 401/erro).</summary>
    public Task<UsageBudgetDto?> GetUsageBudgetAsync(
        CancellationToken ct = default) =>
        GetOrNull<UsageBudgetDto>("/api/v1/usage/budget", ct);

    /// <summary>GET /indexing/status — resumo X/Y (null se 401/erro).</summary>
    public Task<IndexingStatusDto?> GetIndexingStatusAsync(
        CancellationToken ct = default) =>
        GetOrNull<IndexingStatusDto>("/api/v1/indexing/status", ct);

    /// <summary>SPEC-149 RF-004: GET /usage/llm?by=role — breakdown role×model do caller.</summary>
    public Task<LlmUsageBreakdownDto?> GetLlmUsageByRoleAsync(
        CancellationToken ct = default) =>
        GetOrNull<LlmUsageBreakdownDto>("/api/v1/usage/llm?by=role", ct);

    /// <summary>POST /repos/index — dispara indexação do repo (SPEC-60).</summary>
    public async Task<bool> TriggerRepoIndexAsync(
        string repository, string? branch, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("/api/v1/repos/index",
            new { repository, branch }, Json, ct);
        return resp.IsSuccessStatusCode;
    }

    // ---- SPEC-145: repo wiki ----------------------------------------------

    /// <summary>GET /repos/{repo}/wiki — árvore de tópicos (nunca 404: empty tree).</summary>
    public Task<WikiTreeDto?> GetWikiTreeAsync(
        string repository, string? branch, CancellationToken ct = default)
    {
        var path = $"/api/v1/repos/{Uri.EscapeDataString(repository)}/wiki"
            + (string.IsNullOrEmpty(branch)
                ? "" : $"?branch={Uri.EscapeDataString(branch)}");
        return GetOrNull<WikiTreeDto>(path, ct);
    }

    /// <summary>GET /repos/{repo}/wiki/pages/{id} — página completa.</summary>
    public Task<WikiPageDto?> GetWikiPageAsync(
        string repository, Guid pageId, CancellationToken ct = default) =>
        GetOrNull<WikiPageDto>(
            $"/api/v1/repos/{Uri.EscapeDataString(repository)}"
            + $"/wiki/pages/{pageId}", ct);

    /// <summary>POST /repos/{repo}/wiki/generate — 202 (run) | null (409/erro).</summary>
    public async Task<WikiRunDto?> GenerateWikiAsync(
        string repository, string? branch, string? effort,
        CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync(
            $"/api/v1/repos/{Uri.EscapeDataString(repository)}/wiki/generate",
            new { branch, effort }, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        return await resp.Content
            .ReadFromJsonAsync<WikiRunDto>(Json, ct);
    }

    /// <summary>GET /repos/{repo}/wiki/runs/{id} — status do job.</summary>
    public Task<WikiRunDto?> GetWikiRunAsync(
        string repository, Guid runId, CancellationToken ct = default) =>
        GetOrNull<WikiRunDto>(
            $"/api/v1/repos/{Uri.EscapeDataString(repository)}"
            + $"/wiki/runs/{runId}", ct);

    // ---- SPEC-143: code scans ---------------------------------------------

    /// <summary>
    /// POST /scans {repositories[], goal, profile?, effort?} → 202.
    /// null = 4xx (validação/quota — mensagem fica no catch da página).
    /// </summary>
    public async Task<ScanCreatedDto?> CreateScanAsync(
        IReadOnlyList<string> repositories, string goal,
        string? profile, string? effort, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("/api/v1/scans", new
        {
            repositories,
            goal,
            profile,
            effort,
        }, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        return await resp.Content
            .ReadFromJsonAsync<ScanCreatedDto>(Json, ct);
    }

    /// <summary>GET /scans (status/cursor/limit query) — lista cursada.</summary>
    public Task<ScanPageDto?> ListScansAsync(
        string? status = null, string? cursor = null, int limit = 50,
        CancellationToken ct = default)
    {
        var path = $"/api/v1/scans?limit={limit}"
            + (string.IsNullOrEmpty(status)
                ? "" : $"&status={Uri.EscapeDataString(status)}")
            + (string.IsNullOrEmpty(cursor)
                ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
        return GetOrNull<ScanPageDto>(path, ct);
    }

    /// <summary>GET /scans/{id} — detalhe + contagens por severidade.</summary>
    public Task<ScanDto?> GetScanAsync(
        Guid id, CancellationToken ct = default) =>
        GetOrNull<ScanDto>($"/api/v1/scans/{id}", ct);

    /// <summary>GET /scans/{id}/findings — RF-004 filters + cursor.</summary>
    public Task<ScanFindingPageDto?> GetScanFindingsAsync(
        Guid scanId, string? repository = null, string? severity = null,
        string? status = null, string? remediationState = null,
        string? q = null, string? after = null, int first = 200,
        CancellationToken ct = default)
    {
        var path = $"/api/v1/scans/{scanId}/findings?first={first}"
            + (string.IsNullOrEmpty(repository)
                ? "" : $"&repository={Uri.EscapeDataString(repository)}")
            + (string.IsNullOrEmpty(severity)
                ? "" : $"&severity={Uri.EscapeDataString(severity)}")
            + (string.IsNullOrEmpty(status)
                ? "" : $"&status={Uri.EscapeDataString(status)}")
            + (string.IsNullOrEmpty(remediationState)
                ? "" : $"&remediation_state={Uri.EscapeDataString(remediationState)}")
            + (string.IsNullOrEmpty(q)
                ? "" : $"&q={Uri.EscapeDataString(q)}")
            + (string.IsNullOrEmpty(after)
                ? "" : $"&after={Uri.EscapeDataString(after)}");
        return GetOrNull<ScanFindingPageDto>(path, ct);
    }

    /// <summary>PATCH /scan-findings/{id} {status} — triagem.</summary>
    public async Task<ScanFindingDto?> PatchScanFindingAsync(
        Guid id, string status, CancellationToken ct = default)
    {
        var resp = await http.PatchAsJsonAsync(
            $"/api/v1/scan-findings/{id}", new { status }, Json, ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content
                .ReadFromJsonAsync<ScanFindingDto>(Json, ct)
            : null;
    }

    /// <summary>
    /// POST /scan-findings/{id}/fix — 202 {finding_id, conversation_id}
    /// ou null (404/409 — a página decide o toast).
    /// </summary>
    public async Task<ScanFixResultDto?> FixScanFindingAsync(
        Guid id, CancellationToken ct = default)
    {
        var resp = await http.PostAsync(
            $"/api/v1/scan-findings/{id}/fix", null, ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content
                .ReadFromJsonAsync<ScanFixResultDto>(Json, ct)
            : null;
    }

    /// <summary>POST /scan-findings/fix-bulk {ids[]} — 202 items[].</summary>
    public async Task<IReadOnlyList<ScanFixResultDto>?>
        FixScanFindingsBulkAsync(
            IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync(
            "/api/v1/scan-findings/fix-bulk", new { ids }, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        var page = await resp.Content
            .ReadFromJsonAsync<ScanFixBulkDto>(Json, ct);
        return page?.Items;
    }

    // ---- SPEC-140: PR reviews ------------------------------------------------

    /// <summary>
    /// GET /scm/reviews (repository/pr/status + cursor) — runs de
    /// auto-review de PR. <paramref name="pageId"/> = next_page_id.
    /// </summary>
    public Task<ReviewRunPageDto?> ListReviewsAsync(
        string? repository = null, int? pr = null, string? status = null,
        string? pageId = null, int limit = 50,
        CancellationToken ct = default)
    {
        var path = $"/api/v1/scm/reviews?limit={limit}"
            + (string.IsNullOrEmpty(repository)
                ? "" : $"&repository={Uri.EscapeDataString(repository)}")
            + (pr is null ? "" : $"&pr={pr.Value}")
            + (string.IsNullOrEmpty(status)
                ? "" : $"&status={Uri.EscapeDataString(status)}")
            + (string.IsNullOrEmpty(pageId)
                ? "" : $"&page_id={Uri.EscapeDataString(pageId)}");
        return GetOrNull<ReviewRunPageDto>(path, ct);
    }

    /// <summary>GET /scm/reviews/{id} — run + findings do run.</summary>
    public Task<ReviewDetailDto?> GetReviewAsync(
        Guid id, CancellationToken ct = default) =>
        GetOrNull<ReviewDetailDto>($"/api/v1/scm/reviews/{id}", ct);

    /// <summary>
    /// PATCH /scm/reviews/{runId}/findings/{fid} {status} — triagem
    /// (<c>open|dismissed</c>). null = 404/400.
    /// </summary>
    public async Task<ReviewFindingDto?> PatchReviewFindingAsync(
        Guid runId, Guid findingId, string status,
        CancellationToken ct = default)
    {
        var resp = await http.PatchAsJsonAsync(
            $"/api/v1/scm/reviews/{runId}/findings/{findingId}",
            new { status }, Json, ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content
                .ReadFromJsonAsync<ReviewFindingDto>(Json, ct)
            : null;
    }

    // ---- SPEC-144: batch refactors ------------------------------------------

    /// <summary>
    /// POST /refactors {repositories[], goal, strategy?, mode?,
    /// max_concurrency?, batch_size_hint?} → 202. null = 4xx.
    /// </summary>
    public async Task<RefactorCreatedDto?> CreateRefactorAsync(
        IReadOnlyList<string> repositories, string goal,
        string? strategy = null, string? mode = null,
        int? maxConcurrency = null, int? batchSizeHint = null,
        CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("/api/v1/refactors", new
        {
            repositories,
            goal,
            strategy,
            mode,
            max_concurrency = maxConcurrency,
            batch_size_hint = batchSizeHint,
        }, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        return await resp.Content
            .ReadFromJsonAsync<RefactorCreatedDto>(Json, ct);
    }

    /// <summary>GET /refactors (status/cursor/limit) — lista cursada.</summary>
    public Task<RefactorPageDto?> ListRefactorsAsync(
        string? status = null, string? cursor = null, int limit = 50,
        CancellationToken ct = default)
    {
        var path = $"/api/v1/refactors?limit={limit}"
            + (string.IsNullOrEmpty(status)
                ? "" : $"&status={Uri.EscapeDataString(status)}")
            + (string.IsNullOrEmpty(cursor)
                ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
        return GetOrNull<RefactorPageDto>(path, ct);
    }

    /// <summary>GET /refactors/{id} — detalhe + progresso.</summary>
    public Task<RefactorPlanDto?> GetRefactorAsync(
        Guid id, CancellationToken ct = default) =>
        GetOrNull<RefactorPlanDto>($"/api/v1/refactors/{id}", ct);

    /// <summary>GET /refactors/{id}/batches — lotes ordenados por n.</summary>
    public Task<RefactorBatchPageDto?> GetRefactorBatchesAsync(
        Guid id, CancellationToken ct = default) =>
        GetOrNull<RefactorBatchPageDto>(
            $"/api/v1/refactors/{id}/batches", ct);

    /// <summary>
    /// POST /refactors/{id}/{action} — start|pause|resume|cancel.
    /// Retorna o status code para a página decidir o toast (403 no
    /// approval gate, 409 em estado inválido).
    /// </summary>
    public async Task<HttpStatusCode> RefactorActionAsync(
        Guid id, string action, CancellationToken ct = default)
    {
        var resp = await http.PostAsync(
            $"/api/v1/refactors/{id}/{action}", null, ct);
        return resp.StatusCode;
    }

    /// <summary>POST /refactors/{id}/batches/{n}/retry — 202.</summary>
    public async Task<bool> RetryRefactorBatchAsync(
        Guid id, int n, CancellationToken ct = default)
    {
        var resp = await http.PostAsync(
            $"/api/v1/refactors/{id}/batches/{n}/retry", null, ct);
        return resp.IsSuccessStatusCode;
    }
}

/// <summary>SPEC-147 RF-004: resposta de <c>/usage/budget</c>.</summary>
public sealed record UsageBudgetDto(
    long? MonthlyLimit, long Consumed, long? Remaining,
    DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
    IReadOnlyList<ModelUsageDto> ByModel);

/// <summary>SPEC-147 RF-004: consumo por modelo no período.</summary>
public sealed record ModelUsageDto(string Model, long Tokens, decimal CostUsd);

/// <summary>SPEC-147 RF-005: resposta de <c>/indexing/status</c>.</summary>
public sealed record IndexingStatusDto(
    int TotalConnected, int Indexed, int Indexing, int Failed);

/// <summary>SPEC-149 RF-004: linha role×model de <c>/usage/llm</c>.</summary>
public sealed record LlmUsageRowDto(
    string Role, string Model, long Tokens, decimal CostUsd);

/// <summary>SPEC-149 RF-004: resposta de <c>/usage/llm?by=role</c>.</summary>
public sealed record LlmUsageBreakdownDto(
    string By, string Period, IReadOnlyList<LlmUsageRowDto> Rows);

/// <summary>SPEC-145: árvore de tópicos da wiki de um repo.</summary>
public sealed record WikiTreeDto(
    string Repository, string Branch, string? CommitSha,
    IReadOnlyList<WikiTopicNode> Topics);

/// <summary>SPEC-145: nó de tópico (grupo de páginas).</summary>
public sealed record WikiTopicNode(
    string Key, string Title, IReadOnlyList<WikiPageItem> Pages);

/// <summary>SPEC-145: entrada de página na árvore.</summary>
public sealed record WikiPageItem(
    Guid Id, string Topic, string PathKey, string Title,
    bool Stale, int SortOrder);

/// <summary>SPEC-145: página completa (markdown + fontes).</summary>
public sealed record WikiPageDto(
    Guid Id, string Repository, string Branch, string Topic,
    string PathKey, string Title, string ContentMd,
    IReadOnlyList<string> SourceFiles, bool Stale,
    Guid WikiRunId, DateTimeOffset UpdatedAt);

/// <summary>SPEC-145: status do job de geração.</summary>
public sealed record WikiRunDto(
    Guid Id, Guid? OrgId, string Repository, string Branch,
    string? CommitSha, string Effort, string Status, string? StatsJson,
    string? Error, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);

/// <summary>SPEC-143: resposta 202 do POST /scans.</summary>
public sealed record ScanCreatedDto(
    Guid ScanId, Guid? ConversationId,
    Guid? CoordinatorConversationId, string Status);

/// <summary>SPEC-143: página de scans.</summary>
public sealed record ScanPageDto(
    IReadOnlyList<ScanDto> Items, string? NextPageId);

/// <summary>SPEC-143: scan na listagem/detalhe.</summary>
public sealed record ScanDto(
    Guid Id, Guid? OrgId, IReadOnlyList<string> Repositories,
    string Goal, string Profile, string Effort, string Status,
    string Trigger, Guid? CoordinatorConversationId,
    int FindingsCount, IReadOnlyDictionary<string, int> SeverityCounts,
    string? Error, DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

/// <summary>SPEC-143: página de findings.</summary>
public sealed record ScanFindingPageDto(
    IReadOnlyList<ScanFindingDto> Items, string? NextPageId);

/// <summary>SPEC-143: finding triável.</summary>
public sealed record ScanFindingDto(
    Guid Id, Guid ScanId, Guid? OrgId, string Repository, string Path,
    int? StartLine, int? EndLine, string Kind, string Severity,
    double? Confidence, string? Title, string Description,
    string? RemediationHint, string Status, string RemediationState,
    Guid? ConversationId, string? PrUrl, string? Payoff,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>SPEC-143: resultado de um fix (item do bulk também).</summary>
public sealed record ScanFixResultDto(
    Guid FindingId, Guid? ConversationId, string? Error);

/// <summary>SPEC-143: envelope 202 do fix-bulk.</summary>
public sealed record ScanFixBulkDto(
    IReadOnlyList<ScanFixResultDto> Items);

/// <summary>SPEC-140: página de review runs (GET /scm/reviews).</summary>
public sealed record ReviewRunPageDto(
    IReadOnlyList<ReviewRunDto> Items, string? NextPageId);

/// <summary>
/// SPEC-140: run de auto-review de PR. #442: <paramref name="SummaryText"/>
/// = markdown do sumário postado no provider (null em runs antigos).
/// </summary>
public sealed record ReviewRunDto(
    Guid Id, Guid? OrgId, Guid ConnectionId, string Repository,
    int PrNumber, string HeadSha, string? DiffHash, string Status,
    bool Partial, string Trigger, string? RequestedByUserId,
    string? Error, string? SummaryText, int FindingCount,
    DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);

/// <summary>SPEC-140: envelope de GET /scm/reviews/{id}.</summary>
public sealed record ReviewDetailDto(
    ReviewRunDto Run, IReadOnlyList<ReviewFindingDto> Findings);

/// <summary>
/// SPEC-140: finding triável do review (kind/severity/path/line +
/// <c>comment_url</c> = link do review postado no provider). #442:
/// <paramref name="Permalink"/> = blob permalink da localização do
/// finding (<c>{repo}/blob/{head}/{path}#Lx</c>).
/// </summary>
public sealed record ReviewFindingDto(
    Guid Id, Guid ReviewRunId, string Repository, int PrNumber,
    string Kind, string Severity, double? Confidence, string Path,
    int? Line, string? Title, string Body, string? SuggestedPatch,
    string Status, string? CommentUrl, string? Permalink,
    DateTimeOffset CreatedAt);

/// <summary>SPEC-144: resposta 202 do POST /refactors.</summary>
public sealed record RefactorCreatedDto(
    Guid PlanId, Guid? ConversationId,
    Guid? PlannerConversationId, string Status);

/// <summary>SPEC-144: página de planos de refactor.</summary>
public sealed record RefactorPageDto(
    IReadOnlyList<RefactorPlanDto> Items, string? NextPageId);

/// <summary>SPEC-144: plano na listagem/detalhe (state_counts por estado).</summary>
public sealed record RefactorPlanDto(
    Guid Id, Guid? OrgId, IReadOnlyList<string> Repositories,
    string Goal, string Strategy, string Mode, string? RequestedMode,
    int MaxConcurrency, int? BatchSizeHint, string Status,
    int BatchesTotal, IReadOnlyDictionary<string, int> StateCounts,
    int ProgressPercent, Guid? PlannerConversationId,
    Guid? CoordinatorConversationId, IReadOnlyList<string> Warnings,
    string? Error, DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

/// <summary>SPEC-144: página de batches (GET /refactors/{id}/batches).</summary>
public sealed record RefactorBatchPageDto(
    IReadOnlyList<RefactorBatchDto> Items);

/// <summary>SPEC-144: batch de um plano (grafo + PR + filha).</summary>
public sealed record RefactorBatchDto(
    Guid Id, Guid PlanId, int N, string Repository,
    IReadOnlyList<string> Files, IReadOnlyList<int> DependsOn,
    string? Description, string Risk, string? VerifyCmd, string State,
    string? PrUrl, int? PrNumber, Guid? ConversationId, int Attempts,
    string? VerifyLogDigest, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>SPEC-63: sugestão de knowledge na fila.</summary>
public sealed record KnowledgeSuggestionItem(
    Guid Id, string Scope, string? RepoPath, string Title,
    string Content, string Kind, string Status, string? Reason,
    string? Resolution, DateTimeOffset CreatedAt);