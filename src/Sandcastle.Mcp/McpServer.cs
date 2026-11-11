using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sandcastle.Mcp;

/// <summary>
/// Dispatch do protocolo MCP sobre stdio (JSON-RPC 2.0,
/// newline-delimited): <c>initialize</c>, <c>ping</c>, <c>tools/list</c>,
/// <c>tools/call</c>. Tools <c>sc_*</c> expõem conversas/runs/events/
/// skills do Sandcastle via <see cref="ScApiClient"/> — agentes
/// externos (Claude Code, OpenCode, outro Devin) dirigem o produto
/// sem UI. Mapeamento 1:1 com endpoints V1 existentes (SPEC-124 RF-002).
/// </summary>
public sealed class McpServer
{
    private const string StringType = "string";
    private const string ConvId = "conversation_id";
    private const string ConvIdDesc = "Conversation id.";
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
        string line, ScApiClient api, CancellationToken ct)
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
            ["name"] = "sandcastle-mcp",
            ["version"] = "0.1.0",
        },
    };

    private static JsonObject ToolsList() => new()
    {
        ["tools"] = new JsonArray(
            Tool("sc_list_conversations",
                "List app conversations (id, title, status, updated_at).",
                Schema(new JsonObject
                {
                    ["limit"] = Prop("integer",
                        "Max conversations (default 50)."),
                })),
            Tool("sc_get_conversation",
                "Get one conversation by id (status, model, repository).",
                Schema(new JsonObject
                {
                    [ConvId] = Prop(StringType, "Conversation id (GUID)."),
                }, required: [ConvId])),
            Tool("sc_create_conversation",
                "Create a conversation (async start-task): returns a task "
                + "in WORKING state — poll sc_get_task_status until READY.",
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
            Tool("sc_send_message",
                "Send a user message to a conversation (starts a run).",
                Schema(new JsonObject
                {
                    [ConvId] = Prop(StringType, ConvIdDesc),
                    [Content] = Prop(StringType, "Message text."),
                }, required: [ConvId, Content])),
            Tool("sc_list_events",
                "List durable events of a conversation (paged).",
                Schema(new JsonObject
                {
                    [ConvId] = Prop(StringType, ConvIdDesc),
                    ["limit"] = Prop("integer",
                        "Max events (default 50)."),
                }, required: [ConvId])),
            Tool("sc_search_events",
                "Full-text search over a conversation's events.",
                Schema(new JsonObject
                {
                    [ConvId] = Prop(StringType, ConvIdDesc),
                    ["query"] = Prop(StringType, "Search query."),
                }, required: [ConvId, "query"])),
            Tool("sc_get_task_status",
                "Poll a start-task (conversation create) until READY — "
                + "returns task.status and conversation_id when done.",
                Schema(new JsonObject
                {
                    ["task_id"] = Prop(StringType, "Start-task id."),
                }, required: ["task_id"])),
            Tool("sc_stop_run",
                "Request cancellation of the current run on a conversation.",
                Schema(new JsonObject
                {
                    [ConvId] = Prop(StringType, ConvIdDesc),
                }, required: [ConvId])),
            Tool("sc_list_skills",
                "List skills available to agents (repo + user level).",
                Schema(new JsonObject())),
            Tool("sc_list_mcp_servers",
                "List configured MCP servers (name, transport, status).",
                Schema(new JsonObject())),
            Tool("sc_list_models",
                "List LLM models advertised by the gateway.",
                Schema(new JsonObject())),
            Tool("sc_attention_list",
                "Attention inbox: conversations waiting on the user "
                + "(approvals, finished/failed runs).",
                Schema(new JsonObject())),
            // SPEC-145 RF-004 — wiki do repo + Ask mode como tools.
            Tool("sc_wiki_structure",
                "Get the generated wiki topic tree of a repository "
                + "(topics + page ids + stale flags; latest completed run).",
                Schema(new JsonObject
                {
                    ["repository"] = Prop(StringType,
                        "org/repo to read the wiki of."),
                    ["branch"] = Prop(StringType,
                        "Branch (default main)."),
                }, required: ["repository"])),
            Tool("sc_wiki_contents",
                "Get one wiki page (markdown with code citations) — by "
                + "page_id or by topic path (e.g. 'modules/api').",
                Schema(new JsonObject
                {
                    ["repository"] = Prop(StringType,
                        "org/repo the wiki belongs to."),
                    ["page_id"] = Prop(StringType,
                        "Wiki page id (from sc_wiki_structure)."),
                    ["topic"] = Prop(StringType,
                        "Topic path key (e.g. 'overview', 'modules/x')."),
                    ["branch"] = Prop(StringType,
                        "Branch (default main)."),
                }, required: ["repository"])),
            Tool("sc_ask",
                "Ask a question about an indexed repository (Ask mode) — "
                + "returns the answer plus code citations with permalinks.",
                Schema(new JsonObject
                {
                    ["repository"] = Prop(StringType,
                        "org/repo to ask about."),
                    ["question"] = Prop(StringType,
                        "Natural-language question."),
                    ["branch"] = Prop(StringType,
                        "Branch (default main)."),
                }, required: ["repository", "question"]))),
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
        JsonNode? id, JsonNode? @params, ScApiClient api,
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
                "SC_API_KEY ausente — gere uma chave sk-* no app."));
        }

        var (body, error) = name switch
        {
            "sc_list_conversations" => await api.GetAsync(
                "api/v1/app-conversations/", ct),
            "sc_get_conversation" => await api.GetAsync(
                $"api/v1/app-conversations/{Esc(Req(args, ConvId))}", ct),
            "sc_create_conversation" => await api.PostAsync(
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
            "sc_send_message" => await api.PostAsync(
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
            "sc_list_events" => await api.GetAsync(
                $"api/v1/conversation/{Esc(Req(args, ConvId))}/events", ct),
            "sc_search_events" => await api.GetAsync(
                $"api/v1/conversation/{Esc(Req(args, ConvId))}/events/search"
                + $"?q={Esc(Req(args, "query"))}", ct),
            "sc_get_task_status" => await api.GetAsync(
                "api/v1/app-conversations/start-tasks/"
                + $"{Esc(Req(args, "task_id"))}", ct),
            "sc_stop_run" => await api.PostAsync(
                $"api/acp/conversations/{Esc(Req(args, ConvId))}/cancel",
                null, ct),
            "sc_list_skills" => await api.GetAsync("api/v1/skills", ct),
            "sc_list_mcp_servers" => await api.GetAsync(
                "api/v1/mcp/servers", ct),
            "sc_list_models" => await api.GetAsync("api/v1/models", ct),
            "sc_attention_list" => await api.GetAsync(
                "api/v1/app-conversations/attention", ct),
            "sc_wiki_structure" => await api.GetAsync(
                $"api/v1/repos/{Esc(Req(args, "repository"))}/wiki"
                + (Str(args, "branch") is { } bb ? $"?branch={Esc(bb)}" : ""),
                ct),
            "sc_wiki_contents" => await WikiContentsAsync(args, api, ct),
            "sc_ask" => await api.PostAsync("api/v1/ask-sessions", new
            {
                repository = Req(args, "repository"),
                question = Req(args, "question"),
                branch = Str(args, "branch"),
            }, ct),
            _ => (null, $"tool desconhecida: {name}"),
        };

        return error is not null
            ? Ok(id, CallError(error))
            : Ok(id, CallText(body ?? "null"));
    }

    /// <summary>
    /// SPEC-145 RF-004 — <c>sc_wiki_contents</c>: por <c>page_id</c> é um
    /// GET direto; por <c>topic</c> resolve o id na árvore (path_key ou
    /// nome do grupo) e só então busca a página.
    /// </summary>
    private static async Task<(string? Body, string? Error)>
        WikiContentsAsync(JsonNode? args, ScApiClient api,
            CancellationToken ct)
    {
        var repo = Req(args, "repository");
        if (Str(args, "page_id") is { } pid && pid.Length > 0)
        {
            return await api.GetAsync(
                $"api/v1/repos/{Esc(repo)}/wiki/pages/{Esc(pid)}", ct);
        }

        var topic = Str(args, "topic");
        if (string.IsNullOrEmpty(topic))
        {
            return (null, "informe 'topic' ou 'page_id'.");
        }

        var (treeBody, treeErr) = await api.GetAsync(
            $"api/v1/repos/{Esc(repo)}/wiki"
            + (Str(args, "branch") is { } bb ? $"?branch={Esc(bb)}" : ""),
            ct);
        if (treeErr is not null || treeBody is null)
        {
            return (null, treeErr ?? "árvore da wiki indisponível");
        }

        var pageId = FindPageId(treeBody, topic);
        return pageId is null
            ? (null, $"tópico '{topic}' não encontrado na wiki.")
            : await api.GetAsync(
                $"api/v1/repos/{Esc(repo)}/wiki/pages/{Esc(pageId)}", ct);
    }

    /// <summary>Acha o page_id do tópico na árvore (path_key/topic).</summary>
    private static string? FindPageId(string treeJson, string topic)
    {
        try
        {
            var topics = JsonNode.Parse(treeJson)?["topics"]?.AsArray();
            if (topics is null)
            {
                return null;
            }
            foreach (var node in topics)
            {
                var key = node?["key"]?.GetValue<string>();
                var pages = node?["pages"]?.AsArray();
                if (pages is null)
                {
                    continue;
                }
                foreach (var page in pages)
                {
                    var pathKey = page?["path_key"]?.GetValue<string>();
                    var pageTopic = page?["topic"]?.GetValue<string>();
                    if (string.Equals(pathKey, topic,
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pageTopic, topic,
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(key, topic,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return page?["id"]?.GetValue<string>();
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
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