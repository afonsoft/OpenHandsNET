# API Inventory — contratos verificados (upstream)

Extraído de código clonado + OpenAPI publicado (`docs.openhands.dev`, Mintlify).
`/api/v1` prefix em todos. Auth: `Authorization: Bearer <API_KEY>` (APIKeyHeader)
no Cloud; OIDC/cookie no SaaS; PAT local.

## 1. App Server — `openhands/app_server/v1_router.py`

14 routers montados: `event`, `app_conversation`, `pending_message`, `sandbox`,
`sandbox_spec`, `settings`, `secrets`, `user`, `skills`, `webhook` (event_callback),
`web_client`, `git`, `mcp` (test/tool_call/oauth), `config`.

## 2. App Conversations — `app_conversation/app_conversation_router.py` (verificado)

| Método | Path | Notas |
|---|---|---|
| GET | `/app-conversations/search` | `{items[], next_page_id}` |
| GET | `/app-conversations/count` | |
| GET | `/app-conversations` | batch por `ids` → array |
| POST | `/app-conversations` | **start-task async** (abaixo) |
| PATCH | `/app-conversations/{id}` | update metadata |
| POST | `/app-conversations/{id}/send-message` | erros 404/409/410/503 documentados |
| POST | `/app-conversations/{id}/switch_profile` | erros 404/409/502 |
| POST | `/app-conversations/{id}/switch_acp_model` | erros 400/404/409/502/504 |
| DELETE | `/app-conversations/{id}` | |
| POST | `/app-conversations/stream-start` | stream JSON array de task updates |
| GET | `/app-conversations/start-tasks/search` / `…/count` / `…` | polling do start-task |
| GET | `/app-conversations/{id}/file` \| `/files` \| `/git/changes` \| `/git/diff` \| `/skills` \| `/hooks` \| `/download` | proxies p/ agent-server |

### Start task (docs cloud-api + código)

```json
POST /api/v1/app-conversations
{ "initial_message": {"content":[{"type":"text","text":"…"}]},
  "selected_repository": "owner/repo",
  "observability_span_name": "…", "observability_tags": ["…"], "observability_metadata": {} }
→ 200/201 { "id": "<task-uuid>", "status": "WORKING", "app_conversation_id": null, "sandbox_id": null, "created_at": "…" }
```

`status`: `WORKING → WAITING_FOR_SANDBOX → PREPARING_REPOSITORY → SETTING_UP_SKILLS → READY | ERROR`.
Polling: `GET /app-conversations/start-tasks?ids=<task-uuid>` até `READY` (aí `app_conversation_id` preenchido).
Streaming: `POST /app-conversations/stream-start` → array JSON incremental.

Campos da conversa: `id` (uuid), `title`, `sandbox_status` (`STARTING|RUNNING|PAUSED|ERROR|MISSING`),
`execution_status` (`idle|running|finished|error|stuck|waiting_for_confirmation`),
`selected_repository`, `created_at`, `last_updated_at`.

## 3. Events — `app_server/event/event_router.py` + OpenAPI search-events

| Método | Path | Query |
|---|---|---|
| GET | `/conversation/{conversation_id}/events/search` | `kind__eq`, `timestamp__gte`, `timestamp__lt`, `sort_order=TIMESTAMP|TIMESTAMP_DESC`, `page_id`, `limit≤100` → `EventPage{items,next_page_id}` |
| GET | `/conversation/{conversation_id}/events/count` | mesmos filtros |
| GET | `/conversation/{conversation_id}/events` | batch por `ids` (UUIDs) |

`kind__eq` ∈ 19 tipos: `ActionEvent, MessageEvent, ObservationEvent, AgentErrorEvent,
UserRejectObservation, SystemPromptEvent, TokenEvent, StreamingDeltaEvent,
ConversationStateUpdateEvent, ConversationErrorEvent, Condensation,
CondensationRequest, CondensationSummaryEvent, InterruptEvent, PauseEvent,
ServerErrorEvent, ACPToolCallEvent, HookExecutionEvent, LLMCompletionLogEvent`.

Evento base (verificado OpenAPI): `id` (ULID/UUID str), `timestamp`, `source ∈ {agent,user,environment,hook}`,
`parent_id` (árvore/branching), `kind` (discriminador).

## 4. Sandboxes — `app_server/sandbox/sandbox_router.py`

`GET /sandboxes/search`, `GET /sandboxes` (batch), `POST /sandboxes` (start),
`POST /sandboxes/{id}/pause`, `POST /sandboxes/{id}/resume`, `DELETE /sandboxes/{id}`,
`GET /sandboxes/{id}/settings/secrets[/{name}]`.
`sandbox_specs` router: CRUD de specs (imagem, recursos, timeout).

## 5. Webhooks / outros app_server

`POST /events/{conversation_id}` (event_callback), `POST /conversations`,
`GET /secrets`, `POST /mcp-oauth-state`; `pending_messages`, `settings`, `secrets`,
`user`, `skills`, `web_client`, `git`, `config` (health/config públicos).

## 6. Agent Server (in-sandbox) — `software-agent-sdk/openhands-agent-server/openhands/agent_server/`

Routers: `conversation`, `event`, `bash` (tmux), `file`, `git`, `hooks`, `llm`,
`mcp`, `plugins`, `profiles`/`meta_profiles`/`agent_profiles`, `runtime`,
`settings`, `skills`, `sub_agents`, `tool`, `vscode`, `workspace(s)`, `acp`,
`auth`, `init` (warm-pool deferred init, `X-Init-API-Key`), `server_details`,
`canvas_extensions`, `openai` (proxy OpenAI-compat), `provider_connections`.
WS: `sockets.py`/`session_socket.py`/`pub_sub.py`. **Este servidor é o que roda
dentro do sandbox** — no OpenHandsNET equivale ao agente-worker (ver SPEC 03).

## 7. SaaS/Enterprise — `enterprise/server/routes/` (conceitos; Polyform — não portar código)

`auth`, `oauth_v2`, `oauth_device` (device flow p/ CLI), `idp`, `api_keys`,
`billing` (Stripe), `orgs`, `org_invitations`, `org_profiles`, `org_secrets`,
`org_provider_connections`, `super_admins`, `users_v1`, `user_provisioning`,
`quota`, `analytics_events`, `email`, `feature_flags`, `instance_admin`,
`oauth_providers_admin`, `readiness`, `user_app_settings`, `agent_profiles`,
`github_proxy`, `bitbucket_dc_proxy`, `integration/` (github, gitlab, bitbucket,
bitbucket_dc, azure_devops, jira, jira_dc, slack).
`DEPLOYMENT_MODE`: `cloud` vs `self_hosted` — **nunca confundir com `app_mode`** (`enterprise/AGENTS.md`).

## 8. CLI — `OpenHands-CLI` + docs `/usage/cli/cloud`

`openhands login [--server-url]` (OAuth2 Device Flow), `openhands logout`,
`openhands cloud -t "<task>" | -f <file> [--server-url]` (env `OPENHANDS_CLOUD_URL`),
`openhands` (TUI local), `openhands web` (textual-serve), `openhands serve [--gpu] [--mount-cwd]` (Docker GUI), `openhands-acp`.

## 9. Cloud — `OpenHands-Cloud/docs/ARCHITECTURE.md`

Componentes: **OpenHands app** (UI+API+webhooks), **Runtime API** (pool de warm
runtimes, atribui pods), **Runtime pods** (agent-server isolado), **Image Loader**
(daemon de cache). Terceiros: K8s, PostgreSQL, Redis, Keycloak, LiteLLM Proxy,
LB, blob store (S3). Self-host via Replicated/Helm (EKS/GKE/AKS/local-kind).

## 10. V0 → V1 (deprecação 2026-04-01)

| V0 | V1 |
|---|---|
| `POST /api/conversations` | `POST /api/v1/app-conversations` |
| `initial_user_msg` (string) | `initial_message.content[]` |
| `repository` | `selected_repository` |
| `conversation_id` síncrono | start-task → `app_conversation_id` |
