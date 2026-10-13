# SPEC — 04 · REST API & SignalR

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `rest-and-signalr-api` |
| Type | `API / Feature` |
| Stack | `.NET 10 · Minimal APIs · SignalR` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | [#5](https://github.com/afonsoft/OpenHandsNET/issues/5) (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `02_event_stream_spec`, `03_runtime_sandbox_spec` |

## 1. User Story

**As a** consumidor da API OpenHandsNET (UI, CLI, integrações)
**I want** endpoints `/api/v1` compatíveis com o contrato V1 do OpenHands +
realtime via SignalR
**So that** ferramentas que falam a língua do OpenHands funcionem contra o
OpenHandsNET, e a UI receba eventos com reconexão automática.

**Problem context:** upstream usa **start-task assíncrono** (não retorna
`conversation_id` imediato — o guia do usuário está errado aqui), WebSocket cru
ao agent-server, webhooks e SSE em `stream-start`. Decisão OpenHandsNET:
**REST idêntico ao V1** (port) e **SignalR** no edge UI (divergência consciente
documentada — reconexão, fallback SSE/ long-polling, grupos por conversa).

## 2. Scope

**In scope:**
- Grupo `/api/v1/app-conversations` completo (CRUD, start-task, stream-start,
  send-message, switch_profile/switch_acp_model, proxies file/files/git/skills/hooks/download).
- Grupo `/api/v1/conversation/{id}/events` (search/count/batch).
- Grupos `/api/v1/sandboxes`, `/api/v1/sandbox-specs`, `/api/v1/settings`,
  `/api/v1/secrets`, `/api/v1/pending-messages`, `/api/v1/webhooks`,
  `/api/v1/git`, `/api/v1/config`, `/api/v1/user`, `/api/v1/skills`.
- `ConversationHub` SignalR: `JoinConversation(conversationId)` → stream de
  eventos + `ConversationStateUpdate`; terminal channel para stdin/stdout do bash.
- `AppConversationService` + `AppConversationStartTaskService` (background
  hosted service que materializa sandbox→READY).
- Erros V1 exatos: `send-message` → 404/409/410/503 conforme estado do sandbox.

**Out of scope:** routers `mcp/*` (SPEC 07), auth enterprise (SPEC 08), agent-server
in-sandbox HTTP (interno ao worker, não exposto), `web_client` config injector (fase 2).

## 3. Technical Context

**Endpoints verificados** (`migration-plan/research/api-inventory.md` — fonte
`app_conversation_router.py`, `event_router.py`, `sandbox_router.py` + docs).

**Where:** `Api/Endpoints/*` (route groups minimal API), `Api/Hubs/ConversationHub.cs`,
`Application/Conversations/` (services), `Infrastructure/HostedServices/ConversationStartWorker.cs`.

## 4. Requirements

### RF-001: App Conversations (paridade V1)

| Método | Path | Comportamento |
|---|---|---|
| GET | `/api/v1/app-conversations/search` | `{items[], next_page_id}`; filtros `title__eq`, `created_at__gte`, sort, `limit≤100`, `page_id` |
| GET | `/api/v1/app-conversations/count` | mesmo filtro |
| GET | `/api/v1/app-conversations` | batch `?ids=` (máx 100) |
| POST | `/api/v1/app-conversations` | body: `initial_message.content[]`, `selected_repository`, `agent_type`, `llm_model`, `sandbox_spec_id`, `observability_*` → **AppConversationStartTask** `{id,status,app_conversation_id?,sandbox_id?,created_at}` |
| POST | `/api/v1/app-conversations/stream-start` | mesmo body → stream JSON array de updates do task (SSE) |
| GET | `/api/v1/app-conversations/start-tasks[?ids=]` `/search` `/count` | polling |
| PATCH | `/api/v1/app-conversations/{id}` | `title`, `tags`, `pinned` |
| DELETE | `/api/v1/app-conversations/{id}` | archive + agenda delete do sandbox |
| POST | `/api/v1/app-conversations/{id}/send-message` | `{role:"user",content[],run:bool}`; **409** se sandbox ≠ RUNNING (mensagem upstream: resume first), **410** archived, **503** sandbox error |
| POST | `/api/v1/app-conversations/{id}/switch_profile` | 404/409/502 |
| POST | `/api/v1/app-conversations/{id}/switch_acp_model` | 400/404/409/502/504 |
| GET | `/api/v1/app-conversations/{id}/file` `/files` `/git/changes` `/git/diff` `/skills` `/hooks` `/download` | proxies → `ISandboxExecutor` / services |

`status` do task: `WORKING→WAITING_FOR_SANDBOX→PREPARING_REPOSITORY→SETTING_UP_SKILLS→READY|ERROR`.

### RF-002: Events (paridade exata)

`GET /api/v1/conversation/{id}/events/search?kind__eq&timestamp__gte&timestamp__lt&sort_order&page_id&limit(≤100)`
→ `EventPage{items[],next_page_id}`; `…/events/count`; `…/events?ids=` batch.

### RF-003: Sandboxes / Specs / Settings / Secrets

`GET|POST /api/v1/sandboxes[/search]`; `POST /{id}/pause|resume`; `DELETE /{id}`;
`GET /{id}/settings/secrets[/{name}]` (**valor nunca retornado — apenas metadados**);
`sandbox-specs` CRUD; `GET|POST /api/v1/settings`; `GET|PUT|DELETE /api/v1/secrets/{name}`;
`GET /api/v1/config`; `GET /api/v1/user`; `GET /api/v1/skills`;
`GET|POST /api/v1/pending-messages`.

### RF-004: Webhooks (event_callback paridade)

`POST /api/v1/webhooks/events/{conversation_id}` (recebe evento externo → injeta
no stream), `POST /api/v1/webhooks/conversations` (cria conversa via webhook),
`GET /api/v1/webhooks/secrets`, `POST /api/v1/webhooks/mcp-oauth-state`.

### RF-005: SignalR `ConversationHub` (`/hubs/conversations`)

```
client → JoinConversation(conversationId)         // entra no group
server → "event"        (EventBase serializado, ordem garantida)
server → "state_update" ({sandbox_status, execution_status, tokens, step})
server → "terminal_out" ({data})                   // stdout/stderr bash session
client → "terminal_in"  ({data})                   // stdin interativo (se habilitado)
client → SendUserMessage(conversationId, content)
client → ConfirmAction(conversationId, actionId, approved, reason?)  // confirmation mode
```
- Reconnect → `JoinConversation` reenvia backlog a partir de `lastSeenEventId`.
- **Divergência documentada:** upstream usa WS direto ao agent-server +
  `POST /stream-start` SSE; aqui SignalR substitui ambos no edge (SSE mantido só
  no `stream-start` por compat. de clients REST).

### RF-006: Contrato JSON
- `snake_case` nos payloads (paridade upstream: `app_conversation_id`,
  `sandbox_status`, `initial_message`).
- `problem+json` RFC 9457 nos erros, com `detail` espelhando textos upstream
  (ex.: "Sandbox is not running. Resume it first via POST /sandboxes/{id}/resume").

**Invariants:** IDs são UUID/ULID (nunca inteiros); `limit` clamp 1–100;
eventos nunca retornam secrets; endpoints read-mostly ficam sem lock de estado
(ver regra upstream: nunca segurar `ConversationState` lock no event loop —
equivalente: locks fora do hot path via `SemaphoreSlim`/worker).

## 5. API Contract

Definido em RF-001…RF-005 (paridade V1). **Divergências vs upstream:**
WS→SignalR (edge); `switch_acp_model` só se ACP suportado; `stream-start` mantém
SSE (não SignalR) para clients REST puros.

## 6. Critérios de Aceite

- [ ] `POST /app-conversations` → task `WORKING`; polling `start-tasks` → `READY` com `app_conversation_id` preenchido.
- [ ] `send-message` com sandbox `PAUSED` retorna 409 com `detail` upstream.
- [ ] `events/search?kind__eq=MessageEvent&limit=50` pagina via `next_page_id`.
- [ ] Cliente SignalR recebe `event`+`state_update` em tempo real e recupera backlog após reconnect (`lastSeenEventId`).
- [ ] `curl` batch `GET /app-conversations?ids=a,b` retorna array.
- [ ] `stream-start` emite sequência de status até `READY`/`ERROR`.
- [ ] Teste de contrato: respostas validam contra schemas do OpenAPI de referência (`docs/reference/openhands-upstream/`).

## 7. Task Plan

1. DTOs + `AppConversationService` + start-task worker (verify: unit tests).
2. Route groups `/app-conversations`, `/events`, `/sandboxes` (verify: WebApplicationFactory tests de status/erros).
3. `ConversationHub` + join/backlog/terminal/confirm (verify: HubConnection test).
4. Proxies file/git/skills/hooks/download → `ISandboxExecutor` (verify: integration).
5. Webhooks + pending-messages + settings/secrets (verify: contract tests).
6. `stream-start` SSE (verify: curl).
