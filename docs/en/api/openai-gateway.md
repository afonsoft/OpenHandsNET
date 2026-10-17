# OpenAI-compatible gateway — `/v1/chat/completions` (SPEC-40 RF-001)

User-facing gateway: each request creates an ephemeral conversation (or
attaches to an existing one), runs the agent and answers in OpenAI
format. It is not a tool-calling proxy — the agent's internal tools are
not exposed.

```http
POST /v1/chat/completions
Authorization: Bearer sk-…   # SPEC-08 api key (session cookie also works)
```

## Request — compatibility matrix

| Field | Support | Notes |
|---|---|---|
| `model` | ✅ | optional — deployment default; becomes the conversation's `LlmModel` |
| `messages[]` | ✅ | `role`+`content` string or blocks `[{type:"text"}]` |
| `stream` | ✅ | `true` → SSE `chat.completion.chunk` + `data: [DONE]` |
| `n` | ⚠️ | `1` only; `>1` → 400 |
| `tools`/`functions`/`response_format` | ❌ | documented 400 — agent tools don't leak |
| `temperature`, `max_tokens`, `stop`, `seed`… | — | ignored |
| `X-Conversation-Id` (header) | ✅ | attaches to an existing conversation instead of creating one |

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

`finish_reason`: `stop` (FINISHED/STOPPED/PAUSED) or `error`.

## Streaming (stream=true)

`text/event-stream`: first chunk `delta.role:"assistant"`, `delta.content`
chunks fed by `IAgentActivityBus` deltas (SPEC-34), aggregated fallback
from durable events when the model doesn't emit deltas, a final
`finish_reason` + `usage` chunk, and `data: [DONE]`.

## Errors (OpenAI format)

```json
{"error": {"message":"…","type":"…","code":"…"}}
```

| HTTP | type | When |
|---|---|---|
| 400 | `invalid_request_error` | `tools`/`n>1`/no `messages`, invalid body |
| 401 | — | no auth |
| 403 | `permission_error` | `plan_limit_exceeded:<key> (used/limit)` |
| 404/410/409/503 | `invalid_request_error` | attach to a missing/gone/conflicting conversation |
| 502 | `api_error` | start-task failed |
| 504 | `timeout` | internal 10-min deadline without a terminal state |
