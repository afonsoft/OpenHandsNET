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
    public required string ExecutionStatus { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool Pinned { get; init; }
    public bool ConfirmationMode { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
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
