# Domain Candidates — OpenHands → OpenHandsNET

Bounded contexts extraídos dos repositórios upstream clonados, mapeados para
projetos .NET e SPECs. Acoplamento avaliado por direção de dependência (forward-only).

## Mapa de domínios

| # | Domínio | Fonte upstream (evidência) | Projeto .NET | SPEC | Depende de |
|---|---|---|---|---|---|
| D1 | **Fundação / arquitetura** | solução multi-projeto | `OpenHandsNET.*` (5+Cli) | `01_architecture_overview` | — |
| D2 | **Event Stream** | `software-agent-sdk` event union (19 `kind`s, `id` ULID, `source`, `parent_id`); `enterprise/openhands/app_server/event/` | `Domain` + `Infrastructure` | `02_event_stream_spec` | D1 |
| D3 | **Sandbox / Runtime** | `app_server/sandbox/` (Docker local, remoto, warm pool no Cloud); `agent_server` (execução in-sandbox) | `Infrastructure` | `03_runtime_sandbox_spec` | D1 |
| D4 | **API REST + Realtime** | `app_server/v1_router.py` (14 routers `/api/v1`); `agent_server/*_router.py`; WS+SSE upstream | `Api` + `Application` | `04_rest_and_signalr_api_spec` | D2, D3 |
| D5 | **UI estilo Devin** | `OpenHands/src/routes/` (conversation, planner-tab, task-list-tab, browser-tab, files-tab, commits-tab, usage-tab); base `afonsoft/open-webui` Client | `Client` (Blazor WASM) | `05_devin_ui_integration_spec` | D4 |
| D6 | **CLI** | `OpenHands-CLI` (login device-flow, `cloud -t/-f`, `serve`, `web`, `acp`, TUI) | `Cli` | `06_cli_spec` | D4 |
| D7 | **MCP** | `app_server/mcp/` (oauth, test, tool_call routers); `agent_server/mcp_router.py`; SDK `openhands.sdk.mcp` | `Application`+`Api` | `07_mcp_integration_spec` | D4 |
| D8 | **Enterprise / Cloud / RBAC** | `enterprise/server/routes/` (auth, api_keys, billing, orgs*, super_admins, quota, idp, oauth_*); `OpenHands-Cloud` (K8s, Keycloak, Runtime API, warm pool) | `Api`+`Infrastructure` | `08_enterprise_security_rbac` | D4 |
| D9 | **Conversas de aplicação** | `app_server/app_conversation/` (start-task async, send-message, switch_profile, git/files/hooks/skills/download) | `Application`+`Api` | `04` (contratos) + `02` (eventos) | D2, D3 |
| D10 | **Automações** | `OpenHands/automation`, Canvas `automation-*` routes | `Application` | *(fase 2 — fora do corte inicial)* | D4 |

## Bounded contexts internos do núcleo

```
┌─────────────────────────── OpenHandsNET.Api ────────────────────────────┐
│ /api/v1/app-conversations │ /api/v1/conversation/{id}/events │ /hubs/*   │
└───────┬──────────────────────────────┬──────────────────────┬───────────┘
        │ Application                  │                      │
   AppConversationService      EventService            SandboxService
   (start-task, send-msg,      (append, search,        (Docker.DotNet,
    profiles, git/files)        count, subscribe)       pause/resume, specs)
        │                            │                      │
        └──────────────► Domain ◄────┴──────────────────────┘
              Event records (kind), Conversation, Sandbox, StartTask
        ▲
        │ Infrastructure: EF Core (SQLite/PG), webhooks, secrets store, OTel
```

## Acoplamento e direção

- `Domain` não referencia nada (eventos/imutáveis `record`).
- `Application` → `Domain` apenas.
- `Infrastructure` → `Application`+`Domain` (EF Core, Docker.DotNet, Playwright).
- `Api` → `Infrastructure` (DI) e expõe `Client` estático.
- `Client` → `Application` (DTOs/contratos) — regra do open-webui-net.
- `Cli` → contratos REST/SignalR via `HttpClient`/`HubConnection` (sem ref. de projeto ao Api).

## Domínios fora do corte inicial (fase 2+)

- **Automações** (cron/webhook dispatch) — depende de D4+D8 estáveis.
- **Canvas Extensions / VS Code integration** — `canvas_extensions*`, `vscode_router` upstream.
- **ACP (Agent Client Protocol)** — suporte a agentes externos; avaliar demanda.
- **Billing real (Stripe)** — SPECs modelam contratos/entidades; gateway concreto `[A DEFINIR]`.
