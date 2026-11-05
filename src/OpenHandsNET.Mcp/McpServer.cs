using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenHandsNET.Mcp;

/// <summary>
/// Dispatch do protocolo MCP sobre stdio (JSON-RPC 2.0,
/// newline-delimited): <c>initialize</c>, <c>ping</c>, <c>tools/list</c>,
/// <c>tools/call</c>. Tools <c>ohn_*</c> expõem conversas/runs/events/
/// skills do OpenHandsNET via <see cref="OhnApiClient"/> — agentes
/// externos (Claude Code, OpenCode, outro Devin) dirigem o produto
/// sem UI. Mapeamento 1:1 com endpoints V1 existentes (SPEC-124 RF-002).
/// </summary>
public sealed class McpServer
{
    private const string StringType = "string";
    private const string ConvId = "conversation_id";
    private const string Content = "content";
    /// <summary>Versão do protocolo MCP anunciada no handshake.</summary>
    public const string ProtocolVersion = "2024-11-05";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Processa uma linha de request JSON-RPC e devolve a resposta
    /// serializada — null para notifications (sem resposta no protocolo).
    /// </summary>
    public async Task<string?> HandleLineAsync(
        string line, OhnApiClient api, CancellationToken ct)
    {
        JsonNode? id;
        string? method;
        JsonNode? @params;
        try
        {
            var request = JsonNode.Parse(line);
            id = request?["id"]?.DeepClone();
            method = request?["method"]?.GetValue<string>();
            @params = request?["params"];
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error");
        }

        if (method is null)
        {
            return Error(id, -32600, "Invalid request");
        }

        // Notifications: sem id → sem resposta (spec MCP).
        if (id is null)
        {
            return null;
        }

        return method switch
        {
            "initialize" => Ok(id, InitializeResult()),
            "ping" => Ok(id, new JsonObject()),
            "tools/list" => Ok(id, ToolsList()),
            "tools/call" => await ToolsCallAsync(id, @params, api, ct),
            _ => Error(id, -32601, $"Method not found: {method}"),
        };
    }

    private static JsonObject InitializeResult() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject(),
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "openhandsnet-mcp",
            ["version"] = "0.1.0",
        },
    };

    private static JsonObject ToolsList() => new()
    {
        ["tools"] = new JsonArray(
            Tool("ohn_list_conversations",
                "List app conversations (id, title, status, updated_at).",
                Schema(new JsonObject
                {
                    ["limit"] = Prop("integer",
                        "Max conversations (default 50)."),
                })),
            Tool("ohn_get_conversation",
                "Get one conversation by id (status, model, repository).",
                Schema(new JsonObject
                {
                    [ConvId] = Prop(StringType, "Conversation id (GUID)."),
                }, required: [ConvId])),
            Tool("ohn_create_conversation",
                "Create a conversation (async start-task): returns a task "
                + "in WORKING state — poll ohn_get_task_status until READY.",
                Schema(new JsonObject
                {
                    ["initial_prompt"] = Prop(StringType,
                        "User message to seed and run."),
                    ["selected_repository"] = Prop(StringType,
                        "org/repo to bind."),
                    ["llm_model"] = Prop(StringType, "Model id override."),
                    ["confirmation_mode"] = Prop("boolean",
                        "Require approval before the run starts."),
                })),
            Tool("ohn_send_message",
                "Send a user message to a conversation (starts a run).",
                Schema(new JsonObject
                {
                    [ConvId] = Prop(StringType, "Conversation id."),
                    [Content] = Prop(StringType, "Message text."),
                }, required: [ConvId, Content])),
            Tool("ohn_list_events",
                "List durable events of a conversation (paged).",
                Schema(new JsonObject
                {
                    [ConvId] = Prop(StringType, "Conversation id."),
                    ["limit"] = Prop("integer",
                        "Max events (default 50)."),
                }, required: [ConvId])),
            Tool("ohn_search_events",
                "Full-text search over a conversation's events.",
                Schema(new JsonObject
                {
                    [ConvId] = Prop(StringType, "Conversation id."),
                    ["query"] = Prop(StringType, "Search query."),
                }, required: [ConvId, "query"])),
            Tool("ohn_get_task_status",
                "Poll a start-task (conversation create) until READY — "
                + "returns task.status and conversation_id when done.",
                Schema(new JsonObject
                {
                    ["task_id"] = Prop(StringType, "Start-task id."),
                }, required: ["task_id"])),
            Tool("ohn_stop_run",
                "Request cancellation of the current run on a conversation.",
                Schema(new JsonObject
                {
                    [ConvId] = Prop(StringType, "Conversation id."),
                }, required: [ConvId])),
            Tool("ohn_list_skills",
                "List skills available to agents (repo + user level).",
                Schema(new JsonObject())),
            Tool("ohn_list_mcp_servers",
                "List configured MCP servers (name, transport, status).",
                Schema(new JsonObject())),
            Tool("ohn_list_models",
                "List LLM models advertised by the gateway.",
                Schema(new JsonObject())),
            Tool("ohn_attention_list",
                "Attention inbox: conversations waiting on the user "
                + "(approvals, finished/failed runs).",
                Schema(new JsonObject()))),
    };

    private static JsonObject Tool(
        string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
    };

    private static JsonObject Schema(
        JsonObject properties, string[]? required = null) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = required is null
            ? new JsonArray()
            : new JsonArray([.. required.Select(r => (JsonNode)r)]),
    };

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    private async Task<string> ToolsCallAsync(
        JsonNode? id, JsonNode? @params, OhnApiClient api,
        CancellationToken ct)
    {
        var name = @params?["name"]?.GetValue<string>();
        var args = @params?["arguments"];
        if (name is null)
        {
            return Error(id, -32602, "tools/call sem 'name'.");
        }

        if (!api.HasCredentials)
        {
            return Ok(id, CallError(
                "OHN_API_KEY ausente — gere uma chave sk-* no app."));
        }

        var (body, error) = name switch
        {
            "ohn_list_conversations" => await api.GetAsync(
                "api/v1/app-conversations/", ct),
            "ohn_get_conversation" => await api.GetAsync(
                $"api/v1/app-conversations/{Esc(Req(args, ConvId))}", ct),
            "ohn_create_conversation" => await api.PostAsync(
                "api/v1/app-conversations/", new
                {
                    initial_message = Str(args, "initial_prompt") is { } p
                        ? new
                        {
                            role = "user",
                            content = new[] { new { type = "text", text = p } },
                            run = true,
                        }
                        : null,
                    selected_repository = Str(args, "selected_repository"),
                    llm_model = Str(args, "llm_model"),
                    confirmation_mode =
                        args?["confirmation_mode"]?.GetValue<bool>() ?? false,
                }, ct),
            "ohn_send_message" => await api.PostAsync(
                $"api/v1/app-conversations/{Esc(Req(args, ConvId))}/send-message",
                new
                {
                    role = "user",
                    content = new[]
                    {
                        new { type = "text", text = Str(args, Content) ?? "" },
                    },
                    run = true,
                }, ct),
            "ohn_list_events" => await api.GetAsync(
                $"api/v1/conversation/{Esc(Req(args, ConvId))}/events", ct),
            "ohn_search_events" => await api.GetAsync(
                $"api/v1/conversation/{Esc(Req(args, ConvId))}/events/search"
                + $"?q={Esc(Req(args, "query"))}", ct),
            "ohn_get_task_status" => await api.GetAsync(
                "api/v1/app-conversations/start-tasks/"
                + $"{Esc(Req(args, "task_id"))}", ct),
            "ohn_stop_run" => await api.PostAsync(
                $"api/acp/conversations/{Esc(Req(args, ConvId))}/cancel",
                null, ct),
            "ohn_list_skills" => await api.GetAsync("api/v1/skills", ct),
            "ohn_list_mcp_servers" => await api.GetAsync(
                "api/v1/mcp/servers", ct),
            "ohn_list_models" => await api.GetAsync("api/v1/models", ct),
            "ohn_attention_list" => await api.GetAsync(
                "api/v1/app-conversations/attention", ct),
            _ => (null, $"tool desconhecida: {name}"),
        };

        return error is not null
            ? Ok(id, CallError(error))
            : Ok(id, CallText(body ?? "null"));
    }

    private static string Esc(string v) => Uri.EscapeDataString(v);

    private static string? Str(JsonNode? args, string key) =>
        args?[key]?.GetValue<string>();

    /// <summary>Arg obrigatório — devolve marcador inválido quando
    /// ausente (a API responde 404/400 e o texto vai na resposta).</summary>
    private static string Req(JsonNode? args, string key) =>
        Str(args, key) ?? $"__missing_{key}__";

    private static JsonObject CallText(string text) => new()
    {
        [Content] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = text,
        }),
    };

    private static JsonObject CallError(string message) => new()
    {
        [Content] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = message,
        }),
        ["isError"] = true,
    };

    private static string Ok(JsonNode? id, JsonObject result) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result,
        }.ToJsonString(Json);

    private static string Error(JsonNode? id, int code, string message) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        }.ToJsonString(Json);
}
