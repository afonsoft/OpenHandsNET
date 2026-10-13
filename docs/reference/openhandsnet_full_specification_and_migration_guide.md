# OpenHandsNET & OpenHands Ecosystem: Deep Dive, API Reference & .NET Migration Specification

> Documento-fonte fornecido pelo mantenedor (versão original em
> `afonsoft/OpenHandsNET@main`). As SPECs em `.specs/` **corrigem** os pontos
> divergentes do contrato V1 real — ver `migration-plan/research/stack-research.md`
> seção "Divergências deliberadas" (ex.: modelo de eventos V1 vs V0,
> `selected_repository`, start-task assíncrono, .NET 10).

Este documento compila a engenharia reversa completa dos repositórios, documentações e especificações de APIs da família OpenHands (Core, CLI, Enterprise e Cloud), acompanhado da arquitetura de implementação em .NET para o projeto OpenHandsNET (afonsoft/OpenHandsNET), integrado com o layout e chat do Open-WebUI Net (afonsoft/open-webui) com uma interface inspirada no Devin Web.

## Sumário Executivo

- Visão Geral dos Repositórios & Arquitetura Original
- Matriz Comparativa das Versões (Core vs. Enterprise vs. Cloud)
- Arquitetura Central: EventStream & Agent Loop
- Runtime, Sandboxing & Execução de Código
- Especificação Completa das APIs REST & WebSockets / SSE
- OpenHands-CLI & Interação Remota
- Arquitetura Proposta: OpenHandsNET (.NET Core)
- Estrutura da Pasta .specs para o Repositório
- Interface Estilo "Devin Web" integrada com Open-WebUI
- Prompt Especializado para o Agente Autônomo / CLI

## 1. Visão Geral dos Repositórios & Arquitetura Original

### 1.1 OpenHands/OpenHands (Core)

- **Objetivo:** O motor autônomo de engenharia de software (anteriormente OpenDevin).
- **Stack:** Python (FastAPI, Pydantic, LiteLLM, Asyncio), Docker SDK, Playwright, React/Vite frontend.
- **Componentes Principais:**
  - **Agent Controller:** Gerencia o ciclo de vida do agente, delega sub-tarefas e controla o limite de iterações (`max_iterations`).
  - **EventStream:** Hub pub/sub em memória e persistido em arquivo/banco, onde todas as ações (Actions) e observações (Observations) trafegam com identificadores sequenciais e rastreabilidade estrita.
  - **Runtime Engine:** Abstração de ambiente de execução. Suporta execução local em Docker, remote docker, ou ambientes Kubernetes com controle de rede e volumes.
  - **LLM Engine:** Encapsulado via LiteLLM ou chamadas diretas a provedores (OpenAI, Anthropic Claude 3.5/3.7/Opus, Bedrock, Ollama, etc.). Suporta Function/Tool Calling nativo e modo de texto estruturado.
  - **Memory & Condensation:** Mecanismo dinâmico de condensação de contexto quando a janela de tokens atinge o limite (gerando `CondensationSummaryEvent` e descartando eventos obsoletos).

### 1.2 OpenHands/OpenHands-CLI

- **Objetivo:** Interface de linha de comando para terminal headless ou interativo (`oh`).
- **Modos de Operação:**
  - **One-shot** (`oh -t "corrija o bug no auth.py"`): Inicia a conversa, executa em sandbox Docker e encerra com saída limpa ou formato `--json`.
  - **Interactive (tmux/terminal):** Console iterativo com streaming em tempo real das ações e observações.
  - **Cloud Mode** (`oh --cloud`): Conecta diretamente à API do OpenHands Cloud usando API Key (`ALL_HANDS_API_KEY` ou `OPENHANDS_API_KEY`), enviando o workspace remoto para processamento sem requerer Docker local.

### 1.3 OpenHands/enterprise

- **Objetivo:** Solução corporativa para implantações on-premises, VPC ou Kubernetes (EKS/GKE/OpenShift).
- **Características:**
  - **Autenticação & SSO:** Suporte a SAML 2.0, OAuth2/OIDC (Okta, Azure AD / Microsoft Entra ID, GitHub Enterprise).
  - **RBAC & Multi-tenancy:** Permissões baseadas em organizações, equipes e projetos.
  - **Isolamento de Segurança:** Orquestração de sandboxes via Kubernetes Pods efêmeros com gVisor/Kata Containers ou Docker-in-Docker isolado, NetworkPolicies restritas e auditoria de comandos em conformidade com SOC2/ISO27001.
  - **Audit Trails & Observability:** Exportação de traces OpenTelemetry (OTel), logs estruturados e métricas Prometheus.

### 1.4 OpenHands/OpenHands-Cloud

- **Objetivo:** Versão SaaS gerenciada (app.all-hands.dev).
- **Infraestrutura:**
  - Pools elásticos de sandboxes inicializados dinamicamente.
  - Gerenciamento de chaves de API, cotas por usuário e controle de custos de inferência de LLM.
  - Integração nativa com GitHub Apps para criar branches, pull requests e comentar em issues automaticamente.

## 2. Matriz Comparativa das Versões

| Recurso | OpenHands Local/Core | OpenHands CLI | OpenHands Enterprise | OpenHands Cloud (SaaS) | OpenHandsNET (Alvo) |
|---|---|---|---|---|---|
| Linguagem / Stack | Python + React | Python (Typer/Rich) | Python + K8s Operators | Python Microservices | C# / .NET 10 + ASP.NET Core |
| Execução Sandbox | Docker Local | Docker Local ou Cloud | Kubernetes Pods / gVisor | Pool Gerenciado na Nuvem | Docker Local + K8s / WSL2 |
| Controle de Sessão | WebSocket / InMemory | Stdio / Socket | Postgres + Redis + OIDC | Postgres Gerenciado | SQLite/PostgreSQL + EF Core |
| Interface Visual | React Simples | Terminal CLI | Web Portal Corporativo | Web Portal SaaS | Open-WebUI + Devin Multi-Pane |
| Multi-Agente | Sim (CodeAct, Browsing) | Delegado | Sim (Equipes / Workflows) | Sim (GitHub App auto-PR) | Sim (Microsoft Semantic Kernel) |
| Suporte MCP | Sim (Model Context Protocol) | Básico | Sim com Proxy Centralizado | Sim | Sim (Nativo C# via stdio/SSE) |

## 3. Arquitetura Central: EventStream & Agent Loop

O coração do OpenHands é o modelo estrito de EventStream. Nenhuma alteração de estado ocorre fora desse fluxo unidirecional.

```
┌────────────────┐
│   Usuário UI   │
└───────┬────────┘
        │ Envia UserMessageEvent
        ▼
┌────────────────┐
│  EventStream   │ ◄─────── Hub Pub/Sub (Persistência & Broadcast)
└───────┬────────┘
        │ Notifica
        ▼
┌────────────────┐
│     Agent      │ ───────► Executa loop de raciocínio (LLM Call)
└───────┬────────┘
        │ Emite Action (CmdRunAction, FileWriteAction, BrowseURLAction)
        ▼
┌────────────────┐
│    Runtime     │ ───────► Executa no Docker Sandbox / Playwright
└───────┬────────┘
        │ Retorna Observation (CmdOutputObservation, FileReadObservation)
        ▼
┌────────────────┐
│  EventStream   │ ───────► Notifica UI via WebSocket / SSE
└────────────────┘
```

### 3.1 Hierarquia de Eventos (Event Types)

**A. Ações (Action)**

- `NullAction`: No-op para avançar ciclo.
- `MessageAction`: Mensagem textual enviada pelo agente para o usuário.
- `CmdRunAction`: Executa comando shell (`command: string`, `keep_prompt: bool`).
- `FileReadAction`: Lê conteúdo de arquivo (`path: string`, `start: int`, `end: int`).
- `FileWriteAction`: Grava arquivo (`path: string`, `content: string`).
- `BrowseURLAction`: Navega para URL via Playwright (`url: string`).
- `BrowseInteractiveAction`: Interação de browser (`browser_actions: string`).
- `AgentDelegateAction`: Delega tarefa para um sub-agente.
- `AgentFinishAction`: Declara conclusão do objetivo com resumo final.
- `CondensationRequestAction`: Solicita sumarização da memória do agente.

**B. Observações (Observation)**

- `NullObservation`: Resposta nula padrão.
- `CmdOutputObservation`: Retorno do comando de terminal (`command_id`, `exit_code`, `content`).
- `FileReadObservation`: Conteúdo do arquivo lido.
- `FileWriteObservation`: Status da escrita.
- `BrowserOutputObservation`: DOM, screenshot em base64 e status da página.
- `AgentDelegateObservation`: Resposta gerada pelo sub-agente.
- `ErrorObservation`: Erro de execução, exceção do LLM ou timeout.
- `UserRejectObservation`: Rejeição do usuário quando o agente solicita confirmação.

> **Nota da revisão (2026-10-12):** esta hierarquia reflete o formato **V0**
> (deprecado). O modelo V1 implementado nas SPECs usa `kind` discriminador,
> IDs ULID/UUID, `source` e `parent_id` — ver `.specs/02_event_stream_spec.md`.

## 4. Runtime, Sandboxing & Execução de Código

- **Isolamento de Contêiner:** Cada conversa é alocada em um container isolado montando o diretório do projeto (`/workspace`).
- **Ambiente padrão:** Imagem Docker pré-configurada com ferramentas de desenvolvimento (Node, Python, Go, Rust, Git, Bash, curl, jq).
- **Comunicação Sandbox-Host:**
  - Execução remota via Docker Exec API ou socket interno.
  - Streaming de stdout/stderr via WebSockets multiplexados.
- **Segurança Corporativa:**
  - Bloqueio de chamadas perigosas (`sudo`, comandos de rede sem permissão).
  - Modo de confirmação (`confirmation_mode: true`), onde comandos destrutivos (como `git push --force` ou `rm -rf`) exigem aprovação explícita do usuário na UI.

## 5. Especificação Completa das APIs REST & WebSockets / SSE

> **Nota da revisão (2026-10-12):** os payloads de resposta abaixo misturam V0/V1.
> O contrato verificado está em `migration-plan/research/api-inventory.md` e
> `docs/reference/openhands-upstream/` — o start é **assíncrono** (start-task),
> o campo é `selected_repository`, e eventos usam `kind`+UUID.

A API do OpenHands divide-se entre App Server (gerenciamento) e Agent Server / Conversation Runtime (execução).

### 5.1 Endpoints Principais

`POST /api/v1/app-conversations` — Inicia uma nova sessão de conversa e aloca o sandbox.

`GET /api/v1/app-conversations/search` — Lista e filtra conversas (`limit`, `next_page_id`, `status`).

`GET /api/v1/app-conversations/{id}/events/search` — Busca histórico de eventos paginado com filtros.

`POST /api/v1/app-conversations/{id}/action` — Injeta ação manual/confirmação do usuário.

### 5.2 Protocolo WebSocket (`/ws/conversations/{id}`)

Canal bidirecional em tempo real para streaming de eventos e controle do terminal interativo (`user_message`, `event`, `state_update`).

## 6. OpenHands-CLI & Interação Remota

O CLI consome diretamente o protocolo WebSocket ou REST:

- Utiliza um cliente leve em Python (em OpenHandsNET será migrado para CLI em Spectre.Console / System.CommandLine).
- Suporta autenticação via `.openhands/config.toml` ou variáveis de ambiente `OPENHANDS_API_KEY` e `OPENHANDS_BASE_URL`.
- Permite modo headless para CI/CD:
  `openhands-cli run --repo . --task "Implemente os testes" --exit-on-finish`

## 7. Arquitetura Proposta: OpenHandsNET (.NET Core)

A reescrita em .NET (afonsoft/OpenHandsNET) será baseada em C# moderno (.NET 10+), tirando proveito de alta performance assíncrona, multithreading nativo, forte tipagem e ecossistema corporativo robusto.

```
┌────────────────────────────────────────────────────────────────────────┐
│                          OpenHandsNET Solution                         │
├───────────────────┬───────────────────┬────────────────────────────────┤
│   Domain/Core     │   Infrastructure  │        Api + Client (WASM)     │
│  - EventStream    │  - Docker.DotNet  │  - Minimal APIs (REST)         │
│  - Agent Loop     │  - Playwright .NET│  - SignalR Hubs (Real-time)    │
│  - SemanticKernel │  - EF Core        │  - Auth / OIDC / Multi-tenant  │
│  - MCP Protocol   │  - Virtual Term.  │  - Background HostedServices   │
├───────────────────┴───────────────────┴────────────────────────────────┤
│                     OpenHandsNET.CLI (Spectre.Console)                 │
└────────────────────────────────────────────────────────────────────────┘
```

### 7.1 Mapeamento de Bibliotecas Python -> .NET

| Recurso Python (OpenHands) | Equivalente .NET (OpenHandsNET) | Justificativa |
|---|---|---|
| FastAPI + Uvicorn | ASP.NET Core Minimal APIs | Desempenho superior, middleware nativo e DI |
| WebSockets raw | ASP.NET Core SignalR | Reconexão automática, multiplexação, fallback SSE |
| LiteLLM | Microsoft Semantic Kernel | Suporte unificado a OpenAI, Claude, Azure OpenAI, Ollama e streaming |
| Docker SDK | Docker.DotNet | Controle total de sockets e containers |
| Playwright | Microsoft.Playwright | Automação de browser oficial |
| Pydantic | C# Records + System.Text.Json + FluentValidation | Tipagem imutável + validação |
| SQLAlchemy | EF Core | ORM moderno e migrações estruturadas |
| Rich / Typer | Spectre.Console | CLIs elegantes com spinners/tabelas/streaming |

## 8. Estrutura da Pasta .specs para o Repositório

```
.specs/
├── 01_architecture_overview.md       # Arquitetura e bibliotecas .NET
├── 02_event_stream_spec.md           # Actions/Observations (→ modelo V1 kind)
├── 03_runtime_sandbox_spec.md        # Orquestração Docker.DotNet e K8s
├── 04_rest_and_signalr_api_spec.md   # Endpoints REST e SignalR Hubs
├── 05_devin_ui_integration_spec.md   # Visual Devin Web + Open-WebUI
├── 06_cli_spec.md                    # CLI em Spectre.Console
├── 07_mcp_integration_spec.md        # Model Context Protocol em C#
└── 08_enterprise_security_rbac.md    # OIDC, tokens e auditoria
```

## 9. Interface Estilo "Devin Web" integrada com Open-WebUI

O Devin Web destaca-se por oferecer uma visão integrada de desenvolvedor ("All-in-one Cockpit"). O Open-WebUI Net (afonsoft/open-webui) serve de base para a casca (autenticação, listagem de conversas, chat e configurações), expandindo o painel direito para as ferramentas de execução:

- **Chat & Thought Stream:** raciocínio do LLM, checklist de tarefas e chat.
- **Terminal Virtual (xterm.js):** stdout/stderr do container em tempo real.
- **Live Browser Preview:** screenshots base64 do Playwright.
- **Monaco Editor / Diff Viewer:** pré-visualização de arquivos alterados.

## 10. Prompt Especializado para o Agente Autônomo / CLI

Este prompt deve ser utilizado pelo Agent CLI ao trabalhar no repositório `afonsoft/OpenHandsNET`:

> Você é o Arquiteto e Engenheiro Chefe responsável pela criação e implementação do OpenHandsNET.
> 1. **Linguagem & Framework:** C# 14, .NET 10, ASP.NET Core Minimal APIs, SignalR.
> 2. **Arquitetura Base:** ecossistema OpenHands, arquitetura centrada em **EventStream** (modelo V1 `kind` discriminador).
> 3. **Modelos:** Semantic Kernel + MCP; runtime de sandbox com Docker.DotNet e Microsoft.Playwright.
> 4. **Layout & UX:** inspirado no Devin Web, integrado ao `afonsoft/open-webui` — terminal xterm.js, browser live, Monaco diff, chat com thinking blocks.
> 5. **Estrutura de Arquivos:** seguir `.specs/01` a `.specs/08`.
> 6. **Padrões:** Clean Architecture + DDD; `record`, `ValueTask`, `IAsyncEnumerable`, `CancellationToken` obrigatório; testes xUnit + FluentAssertions + Testcontainers.
