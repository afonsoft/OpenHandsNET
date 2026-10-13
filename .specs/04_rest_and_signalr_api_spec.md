# SPEC — 04 · REST API & SignalR

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `rest-and-signalr-api` |
| Type | `API / Feature` |
| Stack | `.NET 10 · Minimal APIs · SignalR` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Done` (PR [#18](https://github.com/afonsoft/OpenHandsNET/pull/18)) |
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

## 8. Deliverables — arquivos a criar/modificar

```text
src/OpenHandsNET.Api/Endpoints/AppConversationEndpoints.cs   # grupo completo RF-001
src/OpenHandsNET.Api/Endpoints/StartTaskEndpoints.cs         # start-tasks search/count/ids
src/OpenHandsNET.Api/Endpoints/EventEndpoints.cs             # events search/count/ids
src/OpenHandsNET.Api/Endpoints/SandboxEndpoints.cs           # sandboxes + sandbox-specs
src/OpenHandsNET.Api/Endpoints/SettingsEndpoints.cs          # settings + secrets
src/OpenHandsNET.Api/Endpoints/MiscEndpoints.cs              # config/user/skills/pending-messages
src/OpenHandsNET.Api/Endpoints/WebhookEndpoints.cs           # events/conversations/secrets/mcp-oauth-state
src/OpenHandsNET.Api/Hubs/ConversationHub.cs                 # RF-005
src/OpenHandsNET.Api/DTOs/                                   # request/response snake_case
src/OpenHandsNET.Application/Conversations/AppConversationService.cs
src/OpenHandsNET.Application/Conversations/AppConversationStartTaskService.cs
src/OpenHandsNET.Infrastructure/HostedServices/ConversationStartWorker.cs  # task→sandbox→READY
src/OpenHandsNET.Infrastructure/HostedServices/TerminalBridgeService.cs    # hub↔BashSession
src/OpenHandsNET.Api/ProblemDetails/OpenHandsNetProblemFactory.cs          # RFC 9457 + textos V1
tests/OpenHandsNET.IntegrationTests/Api/AppConversationApiTests.cs         # WebApplicationFactory
tests/OpenHandsNET.IntegrationTests/Api/EventApiTests.cs
tests/OpenHandsNET.IntegrationTests/Api/SignalRConversationHubTests.cs     # HubConnection client
tests/OpenHandsNET.UnitTests/Api/StartTaskStateMachineTests.cs
```

## 9. Referências e links

### Internas (repo)
- `migration-plan/research/api-inventory.md` — inventário completo §1–§9 (fonte primária).
- `docs/reference/openhands-upstream/cloud-api.md` — extrato da doc cloud-api.
- `docs/reference/openhands-upstream/search-events-openapi.md` — schemas de eventos.
- `docs/reference/openhands-upstream/cli-and-setup.md` — extrato CLI/local-setup.

### Upstream — código-fonte
- App server V1 (app-conversations, eventos, sandboxes, webhooks — **Polyform,
  conceitos apenas**): https://github.com/OpenHands/enterprise/tree/693589e/openhands/app_server/
  (`v1_router.py` + subdirs `app_conversation/`, `event/`, `sandbox/`, `webhook/`…)
- Agent-server in-sandbox (MIT): https://github.com/OpenHands/software-agent-sdk/tree/acca181/openhands-agent-server/openhands/agent_server/
- Cliente TS canônico dos contratos: https://github.com/OpenHands/software-agent-sdk/tree/acca181/clients/typescript

### Documentação oficial
- Cloud API (start-task, polling, auth): https://docs.openhands.dev/openhands/usage/cloud/cloud-api
- Events search OpenAPI: https://docs.openhands.dev/openhands/usage/cloud/api-reference/events/search-events
- OpenHands Cloud: https://docs.openhands.dev/openhands/usage/cloud/openhands-cloud

### Bibliotecas / framework
- Minimal APIs: https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/overview
- Route groups: https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/route-handlers#route-groups
- SignalR intro+hubs: https://learn.microsoft.com/aspnet/core/signalr/introduction ·
  https://learn.microsoft.com/aspnet/core/signalr/hubs
- SignalR auth: https://learn.microsoft.com/aspnet/core/signalr/authn-and-authz
- SignalR .NET client: https://learn.microsoft.com/aspnet/core/signalr/dotnet-client
- `TypedResults.ServerSentEvents` (SSE nativo): https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.typedresults.serversentevents
- Problem details / RFC 9457: https://learn.microsoft.com/aspnet/core/web-api/handle-errors · https://www.rfc-editor.org/rfc/rfc9457.html
- Integration tests (`WebApplicationFactory`): https://learn.microsoft.com/aspnet/core/test/integration-tests
- `IAsyncEnumerable` streaming: https://learn.microsoft.com/dotnet/api/system.collections.generic.iasyncenumerable-1

## 10. Guardrails de execução (Devin)

- REST = **paridade V1** (`snake_case`, mesmos paths/status codes); divergências
  só quando documentadas em §5 (SignalR no edge, `stream-start` SSE).
- `limit` clamp `[1,100]`; `page_id` opaco (base64 do cursor, não offset).
- Erros: `problem+json` com `detail` igual ao texto upstream quando existir.
- Nunca retornar secrets; `secrets` endpoints devolvem só metadados.
- Sem lock de estado no hot path (ver regra upstream FIFOLock): serviço usa
  `Channel`/`SemaphoreSlim` fora do request pipeline.
- Auth obrigatória desde já (SPEC 08 entrega policies completas; aqui, bearer
  `sk-*`/JWT do usuário OSS já exigido — nada de endpoints abertos além de
  `/api/v1/config` e `/api/v1/health`).

## 11. Definition of Done

- [ ] Tabela RF-001 implementada com status codes exatos (incl. 404/409/410/503).
- [ ] `stream-start` emite transições até `READY|ERROR`; polling consistente.
- [ ] Hub: join/backlog (`lastSeenEventId`)/terminal/confirm testados com `HubConnection`.
- [ ] Testes de contrato validam payloads contra schemas de
  `docs/reference/openhands-upstream/` (golden).
- [ ] `dotnet test` verde; SPEC `Done`; issue fechada pelo PR.
