using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Agent.Tools;

/// <summary>
/// SPEC-128 (port OWUIC <c>context_*</c>/<c>memory_*</c> DB-backed):
/// working memory por conversa persistida no banco — sobrevive a troca
/// de sandbox e é inspecionável pela API. Complementa
/// <see cref="BuiltinSpecs.ContextNotes"/> (file-based, morre com o
/// container) e <see cref="BuiltinTools"/> memory (org-scoped).
/// Limites: nota ≤ 32KB, ≤ 200 notas/conversa, memória ≤ 8KB/valor —
/// excesso vira erro estruturado, nunca truncamento silencioso.
/// </summary>
public static class BuiltinContextDb
{
    private const int MaxNoteBytes = 32 * 1024;
    private const int MaxNotesPerConversation = 200;
    private const int MaxValueBytes = 8 * 1024;
    private const int MaxSearchHits = 50;

    /// <summary>Salva uma nota de contexto (título + corpo markdown).</summary>
    public static AgentTool ContextSave() => new(
        new ToolSchema("context_save",
            "Save a context note for this conversation (title + markdown "
            + "body) — working memory persisted in the DB.",
            Schema("""
                {"type":"object","properties":{
                  "title":{"type":"string","description":"Short title (≤200 chars)"},
                  "body":{"type":"string","description":"Markdown body (≤32KB)"}},
                 "required":["title","body"]}
                """)),
        Risky: false,
        Handler: async (ctx, a, ct) =>
        {
            var db = Db(ctx);
            if (db is null)
            {
                return ToolResult.Error("context db indisponível");
            }

            var title = Str(a, "title");
            var body = Str(a, "body");
            if (title is null || body is null)
            {
                return ToolResult.Error("title e body são obrigatórios");
            }
            if (title.Length > 200)
            {
                return ToolResult.Error("title excede 200 chars");
            }
            if (body.Length > MaxNoteBytes)
            {
                return ToolResult.Error("body excede 32KB");
            }

            var count = await db.ContextNotes.CountAsync(
                n => n.ConversationId == ctx.ConversationId, ct);
            if (count >= MaxNotesPerConversation)
            {
                return ToolResult.Error(
                    $"limite de {MaxNotesPerConversation} notas por conversa "
                    + "— delete notas antigas com context_delete");
            }

            var note = new ConversationContextNoteRecord
            {
                Id = Guid.NewGuid(),
                ConversationId = ctx.ConversationId,
                Title = title,
                Body = body,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ContextNotes.Add(note);
            await db.SaveChangesAsync(ct);
            return ToolResult.Ok(new { id = note.Id, saved = true });
        });

    /// <summary>Lê uma nota por id.</summary>
    public static AgentTool ContextGet() => new(
        new ToolSchema("context_get",
            "Read a saved context note by id.",
            Schema("""
                {"type":"object","properties":{
                  "id":{"type":"string","description":"Note id (GUID)"}},
                 "required":["id"]}
                """)),
        Risky: false,
        Handler: async (ctx, a, ct) =>
        {
            var db = Db(ctx);
            if (db is null)
            {
                return ToolResult.Error("context db indisponível");
            }
            if (!Guid.TryParse(Str(a, "id"), out var id))
            {
                return ToolResult.Error("id inválido (GUID esperado)");
            }

            var note = await db.ContextNotes.FirstOrDefaultAsync(
                n => n.ConversationId == ctx.ConversationId && n.Id == id, ct);
            return note is null
                ? ToolResult.Error($"nota '{id}' não encontrada")
                : ToolResult.Ok(new
                {
                    id = note.Id,
                    title = note.Title,
                    body = note.Body,
                    created_at = note.CreatedAt,
                });
        });

    /// <summary>Lista notas da conversa (id + título + data).</summary>
    public static AgentTool ContextList() => new(
        new ToolSchema("context_list",
            "List context notes saved in this conversation.",
            Schema("""{"type":"object","properties":{}}""")),
        Risky: false,
        Handler: async (ctx, a, ct) =>
        {
            var db = Db(ctx);
            if (db is null)
            {
                return ToolResult.Error("context db indisponível");
            }

            // SQLite não ordena DateTimeOffset — ordena em memória
            // (≤200 notas por conversa, cardinalidade pequena).
            var notes = (await db.ContextNotes
                .Where(n => n.ConversationId == ctx.ConversationId)
                .Select(n => new { n.Id, n.Title, n.CreatedAt })
                .ToListAsync(ct))
                .OrderByDescending(n => n.CreatedAt)
                .ToList();
            return ToolResult.Ok(new
            {
                notes = notes.Select(n => new
                {
                    id = n.Id,
                    title = n.Title,
                    created_at = n.CreatedAt,
                }).ToList(),
                count = notes.Count,
            });
        });

    /// <summary>Deleta uma nota por id.</summary>
    public static AgentTool ContextDelete() => new(
        new ToolSchema("context_delete",
            "Delete a saved context note by id.",
            Schema("""
                {"type":"object","properties":{
                  "id":{"type":"string","description":"Note id (GUID)"}},
                 "required":["id"]}
                """)),
        Risky: false,
        Handler: async (ctx, a, ct) =>
        {
            var db = Db(ctx);
            if (db is null)
            {
                return ToolResult.Error("context db indisponível");
            }
            if (!Guid.TryParse(Str(a, "id"), out var id))
            {
                return ToolResult.Error("id inválido (GUID esperado)");
            }

            var removed = await db.ContextNotes
                .Where(n => n.ConversationId == ctx.ConversationId
                    && n.Id == id)
                .ExecuteDeleteAsync(ct);
            return removed == 0
                ? ToolResult.Error($"nota '{id}' não encontrada")
                : ToolResult.Ok(new { deleted = true, id });
        });

    /// <summary>Resumo das notas (títulos + tamanho total).</summary>
    public static AgentTool ContextSummary() => new(
        new ToolSchema("context_summary",
            "Summarize the conversation's context notes (titles + total "
            + "size) — quick recovery after context compaction.",
            Schema("""{"type":"object","properties":{}}""")),
        Risky: false,
        Handler: async (ctx, a, ct) =>
        {
            var db = Db(ctx);
            if (db is null)
            {
                return ToolResult.Error("context db indisponível");
            }

            var notes = (await db.ContextNotes
                .Where(n => n.ConversationId == ctx.ConversationId)
                .Select(n => new { n.Id, n.Title, Size = n.Body.Length })
                .ToListAsync(ct))
                .OrderBy(n => n.Id)
                .ToList();
            return ToolResult.Ok(new
            {
                count = notes.Count,
                total_bytes = notes.Sum(n => n.Size),
                notes = notes.Select(n => new
                {
                    id = n.Id,
                    title = n.Title,
                    bytes = n.Size,
                }).ToList(),
            });
        });

    /// <summary>Define um valor de memória por chave (upsert).</summary>
    public static AgentTool MemorySet() => new(
        new ToolSchema("memory_set",
            "Set a granular working-memory value for this conversation "
            + "(key/value, upsert — ≤8KB per value).",
            Schema("""
                {"type":"object","properties":{
                  "key":{"type":"string","description":"Memory key (≤200 chars)"},
                  "value":{"type":"string","description":"Value (≤8KB)"}},
                 "required":["key","value"]}
                """)),
        Risky: false,
        Handler: async (ctx, a, ct) =>
        {
            var db = Db(ctx);
            if (db is null)
            {
                return ToolResult.Error("context db indisponível");
            }

            var key = Str(a, "key");
            var value = Str(a, "value");
            if (key is null || value is null)
            {
                return ToolResult.Error("key e value são obrigatórios");
            }
            if (key.Length is 0 or > 200)
            {
                return ToolResult.Error("key deve ter 1–200 chars");
            }
            if (value.Length > MaxValueBytes)
            {
                return ToolResult.Error("value excede 8KB");
            }

            var row = await db.ConversationMemories.FirstOrDefaultAsync(
                m => m.ConversationId == ctx.ConversationId
                    && m.Key == key, ct);
            if (row is null)
            {
                db.ConversationMemories.Add(new ConversationMemoryRecord
                {
                    ConversationId = ctx.ConversationId,
                    Key = key,
                    Value = value,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                row.Value = value;
                row.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            return ToolResult.Ok(new { key, set = true });
        });

    /// <summary>Lê um valor de memória por chave.</summary>
    public static AgentTool MemoryGet() => new(
        new ToolSchema("memory_get",
            "Get a working-memory value by key for this conversation.",
            Schema("""
                {"type":"object","properties":{
                  "key":{"type":"string","description":"Memory key"}},
                 "required":["key"]}
                """)),
        Risky: false,
        Handler: async (ctx, a, ct) =>
        {
            var db = Db(ctx);
            if (db is null)
            {
                return ToolResult.Error("context db indisponível");
            }

            var key = Str(a, "key");
            if (key is null)
            {
                return ToolResult.Error("key é obrigatório");
            }

            var row = await db.ConversationMemories.FirstOrDefaultAsync(
                m => m.ConversationId == ctx.ConversationId
                    && m.Key == key, ct);
            return row is null
                ? ToolResult.Error($"memory '{key}' não encontrada")
                : ToolResult.Ok(new
                {
                    key = row.Key,
                    value = row.Value,
                    updated_at = row.UpdatedAt,
                });
        });

    /// <summary>Busca substring em chaves/valores de memória.</summary>
    public static AgentTool MemorySearch() => new(
        new ToolSchema("memory_search",
            "Search this conversation's working memory (substring over "
            + "key and value).",
            Schema("""
                {"type":"object","properties":{
                  "query":{"type":"string","description":"Substring to match"}},
                 "required":["query"]}
                """)),
        Risky: false,
        Handler: async (ctx, a, ct) =>
        {
            var db = Db(ctx);
            if (db is null)
            {
                return ToolResult.Error("context db indisponível");
            }

            var query = Str(a, "query");
            if (string.IsNullOrWhiteSpace(query))
            {
                return ToolResult.Error("query é obrigatório");
            }

            var rows = (await db.ConversationMemories
                .Where(m => m.ConversationId == ctx.ConversationId
                    && (m.Key.Contains(query) || m.Value.Contains(query)))
                .ToListAsync(ct))
                .OrderByDescending(m => m.UpdatedAt)
                .Take(MaxSearchHits)
                .ToList();
            return ToolResult.Ok(new
            {
                count = rows.Count,
                hits = rows.Select(m => new
                {
                    key = m.Key,
                    value = m.Value,
                    updated_at = m.UpdatedAt,
                }).ToList(),
            });
        });

    private static AppDbContext? Db(ToolCallContext ctx) =>
        ctx.Services.GetService<AppDbContext>();

    private static string? Str(JsonElement a, string n) =>
        a.TryGetProperty(n, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}