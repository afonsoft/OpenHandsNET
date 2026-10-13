# Extrato — Cloud API (docs.openhands.dev/openhands/usage/cloud/cloud-api)

> Capturado 2026-10-12. Fonte primária para SPEC 04.

## Auth
API key gerada em app.all-hands.dev → Settings > API Keys (mostrada uma vez).
Header: `Authorization: Bearer <key>`.

## POST /api/v1/app-conversations
```json
{ "initial_message": { "content": [{ "type": "text", "text": "..." }] },
  "selected_repository": "owner/repo",
  "observability_span_name": "...", "observability_tags": ["..."],
  "observability_metadata": {} }
```
Resposta (start task):
```json
{ "id": "task-uuid", "status": "WORKING",
  "app_conversation_id": "conv-uuid | null", "sandbox_id": "...", "created_at": "..." }
```
Status flow: `WORKING → WAITING_FOR_SANDBOX → PREPARING_REPOSITORY → SETTING_UP_SKILLS → READY | ERROR`.

## POST /api/v1/app-conversations/stream-start
Mesmo body → array JSON incremental de updates do task (SSE-like stream).

## Polling
`GET /api/v1/app-conversations/start-tasks?ids=TASK_ID` → até `READY` +
`app_conversation_id` preenchido.
`GET /api/v1/app-conversations?ids=CONV_ID` → array com `sandbox_status`,
`execution_status`, `selected_repository`, `title`.
`GET /api/v1/app-conversations/search?limit=20` → `{items[], next_page_id}`.

## Status
- `sandbox_status`: `STARTING | RUNNING | PAUSED | ERROR | MISSING`
- `execution_status`: `idle | running | finished | error | stuck | waiting_for_confirmation`
- Rate limit: conversas antigas são PAUSADAS ao exceder concorrência.

## V0 → V1 (V0 remoção: 2026-04-01)
| V0 | V1 |
|---|---|
| `POST /api/conversations` | `POST /api/v1/app-conversations` |
| `initial_user_msg` (string) | `initial_message.content[]` |
| `repository` | `selected_repository` |
| `conversation_id` imediato | start-task → `app_conversation_id` |
