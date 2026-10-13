# Extrato — Events Search OpenAPI

> `/openhands/usage/cloud/api-reference/events/search-events`, capturado 2026-10-12.
> Fonte primária para SPEC 02 (modelo de eventos) e SPEC 04 (endpoint).

## GET /api/v1/conversation/{conversation_id}/events/search

Query params:
- `kind__eq` — filtro por tipo de evento (enum abaixo)
- `timestamp__gte` / `timestamp__lt` (date-time)
- `sort_order` = `TIMESTAMP` (default) | `TIMESTAMP_DESC`
- `page_id` — cursor da página anterior
- `limit` — ≤100 (default 100)

Response `EventPage`: `{ items: Event[], next_page_id: string|null }`
Auth: `APIKeyHeader`. Erros: 422 validation.

## Event union (discriminador `kind`)

`ServerErrorEvent`, `ACPToolCallEvent`, `Condensation`, `CondensationRequest`,
`CondensationSummaryEvent`, `ConversationErrorEvent`,
`ConversationStateUpdateEvent`, `HookExecutionEvent`, `LLMCompletionLogEvent`,
`ActionEvent`, `MessageEvent`, `AgentErrorEvent`, `ObservationEvent`,
`UserRejectObservation`, `SystemPromptEvent`, `StreamingDeltaEvent`,
`TokenEvent`, `InterruptEvent`, `PauseEvent`.

## Event base (verificado no schema `ServerErrorEvent-Output`)

```yaml
id: string          # ULID/UUID
timestamp: string   # date-time
source: enum        # agent | user | environment | hook
parent_id: string?  # árvore da conversa; irmãos = branches
kind: const string  # ex. "ServerErrorEvent"
```
