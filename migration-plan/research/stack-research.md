# Stack Research — OpenHands → OpenHandsNET

> Fonte de verdade: código clonado de `github.com/OpenHands/*` (HEAD ~2026-10-12)
> e `docs.openhands.dev`. Evidências citadas como `arquivo:linha` no clone ou URL.

## 1. Topologia real do ecossistema OpenHands (verificado)

A família OpenHands **não é um monorepo**. São repositórios separados com papéis distintos:

| Repositório | Papel | Licença | Evidência |
|---|---|---|---|
| `OpenHands/software-agent-sdk` | **Núcleo**: Python SDK, Agent Server (API REST/WS canônica), ferramentas, workspace | MIT | `software-agent-sdk/AGENTS.md` — "owns the Python SDK and Agent Server: agent and tool behavior, conversations, workspaces, events, and the canonical REST/WebSocket API" |
| `OpenHands/OpenHands` | **Agent Canvas** — frontend React/TypeScript (UI web) | MIT | `OpenHands/AGENTS.md` — "This repository is the OpenHands Agent Canvas React/TypeScript frontend" |
| `OpenHands/OpenHands-CLI` | CLI/TUI (Textual) + modo cloud + `openhands serve`/`web`/`acp` | MIT | `OpenHands-CLI/AGENTS.md` |
| `OpenHands/enterprise` | App Server (`openhands/app_server/`) + módulos SaaS (`server/`, `storage/`, `integrations/`, `migrations/`) | **Polyform Free Trial** (NÃO open source) | `enterprise/AGENTS.md`, `OpenHands-Cloud/README.md` warning |
| `OpenHands/OpenHands-Cloud` | Infra/deploy: Helm charts, Terraform, Replicated | **Polyform Free Trial** | `OpenHands-Cloud/README.md`, `docs/ARCHITECTURE.md` |
| `OpenHands/automation` | Automações (scheduling, webhooks, dispatch) | MIT | `software-agent-sdk/AGENTS.md` (tabela de ownership) |

**Implicação para o OpenHandsNET:** a camada "cloud/enterprise" que o usuário quer replicar
vive em código sob licença **Polyform** — os SPECs devem reproduzir **conceitos e
contratos públicos**, nunca portar código. O núcleo (SDK/Agent Server/Canvas/CLI) é MIT.

## 2. Stack original (source)

| Camada | Tecnologia | Evidência |
|---|---|---|
| Agent Server | Python 3.12, FastAPI, Pydantic, asyncio, LiteLLM, Docker SDK, tmux (bash events), Playwright | `software-agent-sdk/pyproject.toml`, `openhands-agent-server/openhands/agent_server/` |
| App Server (SaaS) | FastAPI, SQLAlchemy + Alembic (PostgreSQL), Keycloak OIDC, Stripe | `enterprise/openhands/app_server/`, `enterprise/server/`, `enterprise/migrations/` |
| Frontend | React 19 + TypeScript, Vite, TanStack Query, Tailwind, react-router | `OpenHands/package.json`, `OpenHands/src/` |
| CLI | Python, Textual TUI, PyInstaller binary | `OpenHands-CLI/` |
| Infra Cloud | Kubernetes + Helm, Runtime API (warm pod pool), Image Loader, PostgreSQL, Redis, Keycloak, LiteLLM Proxy, blob store (S3) | `OpenHands-Cloud/docs/ARCHITECTURE.md` |

## 3. Stack alvo OpenHandsNET (decisão)

Alinhado ao padrão já provado em `afonsoft/open-webui` (migração .NET concluída):

| Camada | Tecnologia | Versão | Justificativa |
|---|---|---|---|
| Runtime | **.NET** | **10 (C# 14)** | Paridade com open-webui-net (`OpenWebUI.slnx`), não .NET 9 do guia original |
| API | ASP.NET Core Minimal APIs | — | Idioma adotado no ecossistema afonsoft |
| Realtime | **SignalR** | — | Substitui WebSocket+SSE do upstream (decisão consciente, ver `04_rest_and_signalr_api_spec.md`) |
| ORM | EF Core | 10 | `DatabaseMigrator` no startup, padrão open-webui-net |
| Banco | SQLite (default) / PostgreSQL (enterprise) | — | SQLite = paridade open-webui-net e zero-dep; Postgres = tier enterprise |
| Frontend | **Blazor WebAssembly** | — | Reusar layout/chat do `afonsoft/open-webui` |
| CSS | Tailwind CSS v4 | — | Idem |
| CLI | Spectre.Console + System.CommandLine | — | Equivalente a Textual/Typer |
| Sandbox | **Docker.DotNet** | — | Equivalente ao Docker SDK Python |
| Browser | **Microsoft.Playwright** | — | Oficial |
| LLM | Microsoft Semantic Kernel + compat. LiteLLM/OpenAI | — | Abstração `IAgentLlmProvider`; LiteLLM-proxy via endpoint OpenAI-compat (integra com `afonsoft/LLMRouter`) |
| MCP | MCP C# SDK (stdio + SSE/HTTP) | — | Protocolo nativo do upstream |
| Testes | xUnit + Shouldly/FluentAssertions + Testcontainers | — | |
| Deploy | Dockerfile multi-stage (padrão `:3032→:8080`) + Helm opcional | — | |

**Divergências deliberadas do guia `openhandsnet_full_specification_and_migration_guide.md`:**

| Guia original | Correção aplicada | Motivo |
|---|---|---|
| .NET 9 / C# 13 | **.NET 10 / C# 14** | Paridade com open-webui-net |
| Projetos `OpenHandsNET.Core/Runtime/Web` | **5 projetos Clean Architecture** (`Domain/Application/Infrastructure/Api/Client`) + `Cli` | Mesma divisão já validada |
| Eventos `id` inteiro + `action/args` (V0) | **Modelo V1**: `kind` discriminator, `id` ULID/UUID, `source`, `parent_id` | API V0 deprecada (remoção 2026-04-01) |
| `repository` no body | **`selected_repository`** | Contrato V1 real |
| Resposta síncrona com `conversation_id` | **Start-task assíncrono** (`WORKING→…→READY`) | Verificado em docs + código |
| `POST /{id}/action` (confirm) | `POST /{id}/send-message` + confirmation via `ConversationStateUpdateEvent` | Endpoint real |

## 4. Versões/compatibilidade relevantes

- OpenHands app image: `docker.openhands.dev/openhands/openhands:1.8`; agent-server image `ghcr.io/openhands/agent-server:1.26.0-python` (docs local-setup).
- API V0 (`/api/conversations`) **deprecada — remoção 2026-04-01**; OpenHandsNET implementa **apenas V1**.
- Agente-server REST API upstream tem política de compat: breaks exigem 5 minor releases de runway (`openhands-agent-server/AGENTS.md`).
