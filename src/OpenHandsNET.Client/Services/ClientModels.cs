using System.Text.Json;

namespace OpenHandsNET.Client.Services;

/// <summary>
/// Envelope de evento V1 no client: mantém o JSON cru (JsonElement) e
/// expõe accessors tipados para os campos comuns + por-kind usados na UI.
/// Não duplica o modelo Domain — renderiza pelo discriminador <c>kind</c>.
/// </summary>
public sealed class ClientEvent
{
    public required JsonElement Raw { get; init; }

    public string Id => Get("id").GetString() ?? "";
    public string Kind => Get("kind").GetString() ?? "Event";
    public string Source => Get("source").GetString() ?? "";
    public string? ParentId => Get("parent_id").ValueKind == JsonValueKind.String
        ? Get("parent_id").GetString() : null;
    /// <summary>SPEC-34: slot de streaming — remove o delta efêmero.</summary>
    public string? ItemId => Get("item_id").ValueKind == JsonValueKind.String
        ? Get("item_id").GetString() : null;
    public DateTimeOffset Timestamp => Get("timestamp").ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(Get("timestamp").GetString(), out var ts)
            ? ts : DateTimeOffset.MinValue;

    public JsonElement Get(string name) =>
        Raw.TryGetProperty(name, out var v) ? v : default;

    public string? Str(string name) =>
        Get(name).ValueKind == JsonValueKind.String ? Get(name).GetString() : null;

    /// <summary>Texto de mensagem (MessageEvent.llm_message.content[].text).</summary>
    public string MessageText()
    {
        var msg = Get("llm_message");
        if (msg.ValueKind != JsonValueKind.Object
            || !msg.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return Str("message") ?? "";
        }

        var sb = new System.Text.StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var t))
            {
                sb.Append(t.GetString());
            }
        }

        return sb.ToString();
    }

    public string Role()
    {
        var msg = Get("llm_message");
        return msg.ValueKind == JsonValueKind.Object
            && msg.TryGetProperty("role", out var r)
                ? r.GetString() ?? "user" : "user";
    }

    /// <summary>ActionEvent.tool.name ou string vazio.</summary>
    public string ToolName() =>
        Get("tool").ValueKind == JsonValueKind.Object
            && Get("tool").TryGetProperty("name", out var n)
                ? n.GetString() ?? "" : "";

    /// <summary>ActionEvent.tool.arguments.command (bash).</summary>
    public string? ToolCommand()
    {
        var tool = Get("tool");
        return tool.ValueKind == JsonValueKind.Object
            && tool.TryGetProperty("arguments", out var args)
            && args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("command", out var c)
                ? c.GetString() : null;
    }
}

/// <summary>Item de lista de conversas (AppConversationDto wire).</summary>
public sealed class ConversationItem
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string? SelectedRepository { get; init; }
    public string? LlmModel { get; init; }
    public string? AgentType { get; init; }
    /// <summary>SPEC-29: overlay de permissões — plan (read-only) | build.</summary>
    public string? Mode { get; init; }
    public required string ExecutionStatus { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool Pinned { get; init; }
    public bool ConfirmationMode { get; init; }
    /// <summary>SPEC-44: attention pendente (null|confirmation|question|failed).</summary>
    public string? NeedsAttention { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>SPEC-44: item do inbox de attention.</summary>
public sealed class AttentionItemDto
{
    public required string ConversationId { get; init; }
    public string? Title { get; init; }
    public required string Kind { get; init; }
    public long Since { get; init; }
    public string? Excerpt { get; init; }
}

/// <summary>SPEC-44: resposta de GET /attention.</summary>
public sealed class AttentionListDto
{
    public int Count { get; init; }
    public IReadOnlyList<AttentionItemDto> Items { get; init; } = [];
}

/// <summary>Start-task DTO wire (WORKING → … → READY|ERROR).</summary>
public sealed class StartTaskItem
{
    public required string Id { get; init; }
    public required string Status { get; init; }
    public string? AppConversationId { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Entrada de árvore de arquivos do workspace.</summary>
public sealed class FileItem
{
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
}

/// <summary>SPEC-42: leitura de arquivo + ETag para optimistic lock.</summary>
public sealed record FileReadResult(
    string Content, string? Etag, bool IsBinary);

/// <summary>SPEC-42: resultado do PUT — Conflict carrega o etag corrente.</summary>
public sealed record FileWriteResult(
    bool Ok, string? Etag, bool Conflict);

/// <summary>SPEC-45: skill resolvida da conversa (repo + user).</summary>
public sealed class SkillItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? ContentMd { get; init; }
    public string? Source { get; init; }
}

/// <summary>SPEC-42: match de <c>files/search</c>.</summary>
public sealed class FileSearchMatch
{
    public required string Path { get; init; }
    public int Line { get; init; }
    public int Col { get; init; }
    public string Preview { get; init; } = "";
}

/// <summary>SPEC-42: resultado de <c>POST /exec</c> one-shot.</summary>
public sealed record ExecResult(
    int ExitCode, string Stdout, string Stderr, bool TimedOut);

/// <summary>SPEC-47: automation (routine) do usuário.</summary>
public sealed record AutomationItem(
    Guid Id, Guid? OrgId, string Name, string Prompt,
    string? LlmModel, string? SelectedRepository, string ScheduleKind,
    string? ScheduleTimeUtc, int IntervalMinutes, int? Weekday,
    bool Enabled, DateTimeOffset? NextRunAt, DateTimeOffset? LastRunAt,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>SPEC-47: execução registrada de uma automation.</summary>
public sealed record AutomationRunItem(
    Guid Id, Guid AutomationId, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, string Status, Guid? ConversationId,
    string? Error);