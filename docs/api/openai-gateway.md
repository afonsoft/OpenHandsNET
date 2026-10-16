# OpenAI-compatible gateway — `/v1/chat/completions` (SPEC-40 RF-001)

Gateway user-facing: cada request cria uma conversa efêmera (ou anexa a
uma existente), roda o agente e responde no formato OpenAI. Não é proxy
de tool-calling — tools internas do agente não são expostas.

```http
POST /v1/chat/completions
Authorization: Bearer sk-…   # api key da SPEC-08 (cookie session também vale)
```

## Request — matriz de compatibilidade

| Campo | Suporte | Notas |
|---|---|---|
| `model` | ✅ | opcional — default do deployment; vira `LlmModel` da conversa |
| `messages[]` | ✅ | `role`+`content` string ou blocks `[{type:"text"}]` |
| `stream` | ✅ | `true` → SSE `chat.completion.chunk` + `data: [DONE]` |
| `n` | ⚠️ | só `1`; `>1` → 400 |
| `tools`/`functions`/`response_format` | ❌ | 400 documentado — tools do agente não vazam |
| `temperature`, `max_tokens`, `stop`, `seed`… | — | ignorados |
| `X-Conversation-Id` (header) | ✅ | anexa à conversa existente em vez de criar |

## Response (stream=false)

```json
{
  "id": "chatcmpl-<conversationId>",
  "object": "chat.completion",
  "created": 1760000000,
  "model": "…",
  "choices": [{"index":0,"message":{"role":"assistant","content":"…"},
              "finish_reason":"stop"}],
  "usage": {"prompt_tokens":0,"completion_tokens":0,"total_tokens":0}
}
```

`finish_reason`: `stop` (FINISHED/STOPPED/PAUSED) ou `error`.

## Streaming (stream=true)

`text/event-stream`: primeiro chunk `delta.role:"assistant"`, chunks
`delta.content` alimentados pelos deltas do `IAgentActivityBus` (SPEC-34),
fallback agregado dos eventos durable quando o modelo não emite deltas,
chunk final `finish_reason` + `usage`, e `data: [DONE]`.

## Erros (formato OpenAI)

```json
{"error": {"message":"…","type":"…","code":"…"}}
```

| HTTP | type | Quando |
|---|---|---|
| 400 | `invalid_request_error` | `tools`/`n>1`/sem `messages`, body inválido |
| 401 | — | sem auth |
| 403 | `permission_error` | `plan_limit_exceeded:<key> (used/limit)` |
| 404/410/409/503 | `invalid_request_error` | attach em conversa missing/gone/conflito |
| 502 | `api_error` | start-task falhou |
| 504 | `timeout` | deadline interno de 10min sem terminal |

## ACP — `/api/acp/*` (RF-005)

Subset REST+SSE do Agent Client Protocol (editores tipo Zed):

```text
POST /api/acp/conversations            {initial_prompt|content[], model?, selected_repository?}
GET  /api/acp/conversations/{id}       → {conversation_id, state, title}
POST /api/acp/conversations/{id}/prompt    {prompt|content[]} → 202
POST /api/acp/conversations/{id}/cancel    → 204
GET  /api/acp/conversations/{id}/events    → SSE session/update
```

`session/update`: `agent_message_chunk` (texto do agente),
`current_mode_update` (mudança de `execution_status`) e `turn_complete`
(estado terminal — o stream encerra). Conversas ACP são conversas
normais: aparecem na UI e seguem o mesmo plan enforcement.

## Métricas (RF-006)

- `openhandsnet_gateway_requests_total{model}`
- `openhandsnet_gateway_request_seconds{model,stream}` (histograma)
- `/activity` do sandbox-agent: `sandbox_surface` (liga/desliga de
  surfaces — ver `docs/deployment/surfaces.md`).
