# OpenHandsNET.Mcp — servidor MCP stdio

Expõe o OpenHandsNET como ferramenta para agentes externos (SPEC-124):
Claude Code, OpenCode, Devin ou qualquer cliente MCP dirige conversas,
runs e skills sem UI — o mesmo papel do `OpenWebUI.Mcp` no fork irmão.

## Configuração

```json
// .mcp.json (Claude Code / OpenCode / harness compatível)
{
  "mcpServers": {
    "openhandsnet": {
      "type": "stdio",
      "command": "openhandsnet",
      "args": ["mcp", "serve"],
      "env": {
        "OHN_API_URL": "http://localhost:8080",
        "OHN_API_KEY": "sk-..."
      }
    }
  }
}
```

`OHN_API_KEY` é uma chave `sk-*` emitida pelo app (Settings → API keys).
Sem a chave os tools falham fechado com mensagem explícita.

## Tools expostos

| Tool | Endpoint V1 |
|---|---|
| `ohn_list_conversations` | `GET /api/v1/app-conversations/` |
| `ohn_get_conversation` | `GET /api/v1/app-conversations/{id}` |
| `ohn_create_conversation` | `POST /api/v1/app-conversations/` (start-task async) |
| `ohn_send_message` | `POST /api/v1/app-conversations/{id}/send-message` |
| `ohn_list_events` | `GET /api/v1/conversation/{id}/events` |
| `ohn_search_events` | `GET /api/v1/conversation/{id}/events/search?q=` |
| `ohn_get_task_status` | `GET /api/v1/app-conversations/start-tasks/{id}` |
| `ohn_stop_run` | `POST /api/acp/conversations/{id}/cancel` |
| `ohn_list_skills` | `GET /api/v1/skills` |
| `ohn_list_mcp_servers` | `GET /api/v1/mcp/servers` |
| `ohn_list_models` | `GET /api/v1/models` |
| `ohn_attention_list` | `GET /api/v1/app-conversations/attention` |

Start-task é **assíncrono**: `ohn_create_conversation` retorna uma task
`WORKING` — faça polling com `ohn_get_task_status` até `READY` e leia o
`conversation_id`. Nunca trate o retorno como id síncrono.

## Bootstrap de contexto

`GET /gen-context/presets.json` (estático, sem auth) descreve as
capabilities do produto para um agente externo se orientar — endpoints,
modelo de runtime (eventos duráveis, sandbox, `*.agent.md`) e o bloco
`.mcp.json` acima.
