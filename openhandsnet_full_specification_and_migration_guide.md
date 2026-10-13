# OpenHandsNET & OpenHands Ecosystem: Deep Dive, API Reference & .NET Migration Specification

Este documento compila a engenharia reversa completa dos repositórios, documentações e especificações de APIs da família **OpenHands** (Core, CLI, Enterprise e Cloud), acompanhado da arquitetura de implementação em **.NET** para o projeto **OpenHandsNET** (`afonsoft/OpenHandsNET`), integrado com o layout e chat do **Open-WebUI Net** (`afonsoft/open-webui`) com uma interface inspirada no **Devin Web**.

---

## Sumário Executivo

1. [Visão Geral dos Repositórios & Arquitetura Original](#1-visão-geral-dos-repositórios--arquitetura-original)
2. [Matriz Comparativa das Versões (Core vs. Enterprise vs. Cloud)](#2-matriz-comparativa-das-versões)
3. [Arquitetura Central: EventStream & Agent Loop](#3-arquitetura-central-eventstream--agent-loop)
4. [Runtime, Sandboxing & Execução de Código](#4-runtime-sandboxing--execução-de-código)
5. [Especificação Completa das APIs REST & WebSockets / SSE](#5-especificação-completa-das-apis-rest--websockets--sse)
6. [OpenHands-CLI & Interação Remota](#6-openhands-cli--interação-remota)
7. [Arquitetura Proposta: OpenHandsNET (.NET Core)](#7-arquitetura-proposta-openhandsnet-net-core)
8. [Estrutura da Pasta `.specs` para o Repositório](#8-estrutura-da-pasta-specs-para-o-repositório)
9. [Interface Estilo "Devin Web" integrada com Open-WebUI](#9-interface-estilo-devin-web-integrada-com-open-webui)
10. [Prompt Especializado para o Agente Autônomo / CLI](#10-prompt-especializado-para-o-agente-autônomo--cli)

---

## 1. Visão Geral dos Repositórios & Arquitetura Original

### 1.1 `OpenHands/OpenHands` (Core)
* **Objetivo:** O motor autônomo de engenharia de software (anteriormente *OpenDevin*).
* **Stack:** Python (FastAPI, Pydantic, LiteLLM, Asyncio), Docker SDK, Playwright, React/Vite frontend.
* **Componentes Principais:**
  * **Agent Controller:** Gerencia o ciclo de vida do agente, delega sub-tarefas e controla o limite de iterações (`max_iterations`).
  * **EventStream:** Hub pub/sub em memória e persistido em arquivo/banco, onde todas as ações (*Actions*) e observações (*Observations*) trafegam com identificadores sequenciais e rastreabilidade estrita.
  * **Runtime Engine:** Abstração de ambiente de execução. Suporta execução local em Docker, remote docker, ou ambientes Kubernetes com controle de rede e volumes.
  * **LLM Engine:** Encapsulado via LiteLLM ou chamadas diretas a provedores (OpenAI, Anthropic Claude 3.5/3.7/Opus, Bedrock, Ollama, etc.). Suporta Function/Tool Calling nativo e modo de texto estruturado.
  * **Memory & Condensation:** Mecanismo dinâmico de condensação de contexto quando a janela de tokens atinge o limite (gerando `CondensationSummaryEvent` e descartando eventos obsoletos).

### 1.2 `OpenHands/OpenHands-CLI`
* **Objetivo:** Interface de linha de comando para terminal headless ou interativo (`oh`).
* **Modos de Operação:**
  * **One-shot (`oh -t "corrija o bug no auth.py"`):** Inicia a conversa, executa em sandbox Docker e encerra com saída limpa ou formato `--json`.
  * **Interactive (tmux/terminal):** Console iterativo com streaming em tempo real das ações e observações.
  * **Cloud Mode (`oh --cloud`):** Conecta diretamente à API do OpenHands Cloud usando API Key (`ALL_HANDS_API_KEY` ou `OPENHANDS_API_KEY`), enviando o workspace remoto para processamento sem requerer Docker local.

### 1.3 `OpenHands/enterprise`
* **Objetivo:** Solução corporativa para implantações on-premises, VPC ou Kubernetes (EKS/GKE/OpenShift).
* **Características:**
  * **Autenticação & SSO:** Suporte a SAML 2.0, OAuth2/OIDC (Okta, Azure AD / Microsoft Entra ID, GitHub Enterprise).
  * **RBAC & Multi-tenancy:** Permissões baseadas em organizações, equipes e projetos.
  * **Isolamento de Segurança:** Orquestração de sandboxes via Kubernetes Pods efêmeros com gVisor/Kata Containers ou Docker-in-Docker isolado, NetworkPolicies restritas e auditoria de comandos em conformidade com SOC2/ISO27001.
  * **Audit Trails & Observability:** Exportação de traces OpenTelemetry (OTel), logs estruturados e métricas Prometheus.

### 1.4 `OpenHands/OpenHands-Cloud`
* **Objetivo:** Versão SaaS gerenciada (`app.all-hands.dev`).
* **Infraestrutura:**
  * Pools elásticos de sandboxes inicializados dinamicamente.
  * Gerenciamento de chaves de API, cotas por usuário e controle de custos de inferência de LLM.
  * Integração nativa com GitHub Apps para criar branches, pull requests e comentar em issues automaticamente.

---

## 2. Matriz Comparativa das Versões

| Recurso | OpenHands Local/Core | OpenHands CLI | OpenHands Enterprise | OpenHands Cloud (SaaS) | OpenHandsNET (Alvo) |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Linguagem / Stack** | Python + React | Python (Typer/Rich) | Python + K8s Operators | Python Microservices | **C# / .NET 9 + ASP.NET Core** |
| **Execução Sandbox** | Docker Local | Docker Local ou Cloud | Kubernetes Pods / gVisor | Pool Gerenciado na Nuvem | **Docker Local + K8s / WSL2** |
| **Controle de Sessão** | WebSocket / InMemory | Stdio / Socket | Postgres + Redis + OIDC | Postgres Gerenciado | **PostgreSQL + EF Core + Redis** |
| **Interface Visual** | React Simples | Terminal CLI | Web Portal Corporativo | Web Portal SaaS | **Open-WebUI + Devin Multi-Pane** |
| **Multi-Agente** | Sim (CodeAct, Browsing) | Delegado | Sim (Equipes / Workflows) | Sim (GitHub App auto-PR) | **Sim (Microsoft Semantic Kernel)** |
| **Suporte MCP** | Sim (Model Context Protocol)| Básico | Sim com Proxy Centralizado| Sim | **Sim (Nativo C# via stdio/SSE)** |

---

## 3. Arquitetura Central: EventStream & Agent Loop

O coração do OpenHands é o modelo estrito de **EventStream**. Nenhuma alteração de estado ocorre fora desse fluxo unidirecional.

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

#### A. Ações (`Action`)
* `NullAction`: No-op para avançar ciclo.
* `MessageAction`: Mensagem textual enviada pelo agente para o usuário.
* `CmdRunAction`: Executa comando shell (`command: string`, `keep_prompt: bool`).
* `FileReadAction`: Lê conteúdo de arquivo (`path: string`, `start: int`, `end: int`).
* `FileWriteAction`: Grava arquivo (`path: string`, `content: string`).
* `BrowseURLAction`: Navega para URL via Playwright (`url: string`).
* `BrowseInteractiveAction`: Interação de browser (`browser_actions: string`).
* `AgentDelegateAction`: Delega tarefa para um sub-agente.
* `AgentFinishAction`: Declara conclusão do objetivo com resumo final.
* `CondensationRequestAction`: Solicita sumarização da memória do agente.

#### B. Observações (`Observation`)
* `NullObservation`: Resposta nula padrão.
* `CmdOutputObservation`: Retorno do comando de terminal (`command_id`, `exit_code`, `content`).
* `FileReadObservation`: Conteúdo do arquivo lido.
* `FileWriteObservation`: Status da escrita.
* `BrowserOutputObservation`: DOM, screenshot em base64 e status da página.
* `AgentDelegateObservation`: Resposta gerada pelo sub-agente.
* `ErrorObservation`: Erro de execução, exceção do LLM ou timeout.
* `UserRejectObservation`: Rejeição do usuário quando o agente solicita confirmação.

---

## 4. Runtime, Sandboxing & Execução de Código

1. **Isolamento de Contêiner:** Cada conversa é alocada em um container isolado montando o diretório do projeto (`/workspace`).
2. **Ambiente padrão:** Imagem Docker pré-configurada com ferramentas de desenvolvimento (Node, Python, Go, Rust, Git, Bash, curl, jq).
3. **Comunicação Sandbox-Host:**
   * Execução remota via Docker Exec API ou socket interno.
   * Streaming de stdout/stderr via WebSockets multiplexados.
4. **Segurança Corporativa:**
   * Bloqueio de chamadas perigosas (`sudo`, comandos de rede sem permissão).
   * Modo de confirmação (`confirmation_mode: true`), onde comandos destrutivos (como `git push --force` ou `rm -rf`) exigem aprovação explícita do usuário na UI.

---

## 5. Especificação Completa das APIs REST & WebSockets / SSE

A API do OpenHands divide-se entre **App Server** (gerenciamento) e **Agent Server / Conversation Runtime** (execução).

### 5.1 Endpoints Principais

#### `POST /api/v1/app-conversations`
Inicia uma nova sessão de conversa e aloca o sandbox.

* **Headers:** `Authorization: Bearer <API_KEY>`, `Content-Type: application/json`
* **Request Body:**
```json
{
  "initial_message": {
    "content": [
      {
        "type": "text",
        "text": "Analise o arquivo Program.cs e adicione tratamento de logs com Serilog."
      }
    ]
  },
  "repository": "https://github.com/afonsoft/OpenHandsNET.git",
  "branch": "main",
  "sandbox_config": {
    "image": "openhands/runtime:latest",
    "timeout_minutes": 60,
    "memory_limit_mb": 4096
  },
  "observability_tags": ["dotnet-core", "feature-refactoring"]
}
```
* **Response (201 Created):**
```json
{
  "conversation_id": "conv_98fbc123-4567-89ab-cdef-0123456789ab",
  "status": "STARTING",
  "execution_status": "idle",
  "created_at": "2026-10-12T21:30:00Z",
  "websocket_url": "wss://app.all-hands.dev/ws/conversations/conv_98fbc123-4567-89ab-cdef-0123456789ab"
}
```

#### `GET /api/v1/app-conversations/search`
Lista e filtra conversas existentes.
* **Query Params:** `limit=20`, `next_page_id=string`, `status=RUNNING`
* **Response (200 OK):**
```json
{
  "items": [
    {
      "conversation_id": "conv_98fbc123-4567-89ab-cdef-0123456789ab",
      "sandbox_status": "RUNNING",
      "execution_status": "running",
      "title": "Refatoração de Logs com Serilog",
      "created_at": "2026-10-12T21:30:00Z",
      "last_updated_at": "2026-10-12T21:32:15Z"
    }
  ],
  "next_page_id": null
}
```

#### `GET /api/v1/app-conversations/{id}/events/search`
Busca histórico de eventos paginado com filtros de fonte e tipo.
* **Query Params:** `start_id=0`, `limit=50`, `event_types=CmdRunAction,CmdOutputObservation`
* **Response (200 OK):**
```json
{
  "events": [
    {
      "id": 1,
      "timestamp": "2026-10-12T21:30:05Z",
      "source": "agent",
      "action": "run",
      "args": {
        "command": "dotnet --version"
      }
    },
    {
      "id": 2,
      "timestamp": "2026-10-12T21:30:06Z",
      "source": "environment",
      "observation": "run",
      "content": "9.0.100\n",
      "extras": {
        "exit_code": 0
      }
    }
  ],
  "total_count": 2
}
```

#### `POST /api/v1/app-conversations/{id}/action`
Injeta uma ação manual ou confirmação do usuário (quando em estado `waiting_for_confirmation`).
* **Request Body:**
```json
{
  "action": "confirm",
  "rejection_reason": null
}
```

### 5.2 Protocolo WebSocket (`/ws/conversations/{id}`)
Canal bidirecional em tempo real para streaming de eventos e controle do terminal interativo.

1. **Client -> Server (Mensagem do Usuário / Input de Terminal):**
```json
{
  "type": "user_message",
  "data": {
    "message": "Continue com os testes unitários."
  }
}
```
2. **Server -> Client (Event Notification):**
```json
{
  "type": "event",
  "event": {
    "id": 14,
    "source": "agent",
    "timestamp": "2026-10-12T21:32:00Z",
    "action": "run",
    "args": {
      "command": "dotnet test --logger console;verbosity=normal"
    }
  }
}
```
3. **Server -> Client (Conversation State Update):**
```json
{
  "type": "state_update",
  "state": {
    "sandbox_status": "RUNNING",
    "execution_status": "running",
    "current_step": 7,
    "max_steps": 50,
    "tokens_consumed": 14250
  }
}
```

---

## 6. OpenHands-CLI & Interação Remota

O CLI consome diretamente o protocolo WebSocket ou REST:
* Utiliza um cliente leve em Python (que em **OpenHandsNET** será migrado para um CLI em **Spectre.Console** / `System.CommandLine`).
* Suporta autenticação via `.openhands/config.toml` ou variáveis de ambiente `OPENHANDS_API_KEY` e `OPENHANDS_BASE_URL`.
* Permite modo headless para CI/CD (GitHub Actions / GitLab CI):
  ```bash
  openhands-cli run --repo . --task "Implemente os testes de integração do repositório" --exit-on-finish
  ```

---

## 7. Arquitetura Proposta: OpenHandsNET (.NET Core)

A reescrita em .NET (`afonsoft/OpenHandsNET`) será baseada em C# moderno (.NET 9+), tirando proveito de alta performance assíncrona, multithreading nativo, forte tipagem e ecossistema corporativo robusto.

```
┌────────────────────────────────────────────────────────────────────────┐
│                          OpenHandsNET Solution                         │
├───────────────────┬───────────────────┬────────────────────────────────┤
│   OpenHandsNET.   │   OpenHandsNET.   │        OpenHandsNET.Web        │
│       Core        │      Runtime      │           (ASP.NET)            │
│  - EventStream    │  - Docker.DotNet  │  - Minimal APIs (REST)         │
│  - Agent Loop     │  - Kubernetes C#  │  - SignalR Hubs (Real-time)    │
│  - SemanticKernel │  - Playwright .NET│  - Auth / OIDC / Multi-tenant  │
│  - MCP Protocol   │  - Virtual Terminal│  - Background HostedServices  │
├───────────────────┴───────────────────┴────────────────────────────────┤
│                     OpenHandsNET.CLI (Spectre.Console)                 │
└────────────────────────────────────────────────────────────────────────┘
```

### 7.1 Mapeamento de Bibliotecas Python -> .NET

| Recurso Python (OpenHands) | Equivalente .NET 9 (OpenHandsNET) | Justificativa |
| :--- | :--- | :--- |
| `FastAPI` + `Uvicorn` | **ASP.NET Core Minimal APIs** | Desempenho superior, middleware nativo e injeção de dependência. |
| `WebSockets` raw | **ASP.NET Core SignalR** | Reconexão automática, multiplexação de canais e fallback SSE. |
| `LiteLLM` | **Microsoft Semantic Kernel** | Suporte unificado a OpenAI, Claude, Azure OpenAI, Ollama e streaming. |
| `Docker SDK for Python` | **Docker.DotNet** | Biblioteca C# oficial para controle total de sockets e containers Docker. |
| `Playwright Python` | **Microsoft.Playwright** | Navegação headless e automação de browser oficial da Microsoft. |
| `Pydantic` | **C# Records + System.Text.Json + FluentValidation** | Tipagem imutável com validação estrita em tempo de compilação. |
| `SQLAlchemy` | **Entity Framework Core (EF Core) + Npgsql** | ORM moderno e migrações estruturadas para PostgreSQL. |
| `Rich` / `Typer` | **Spectre.Console** | Criação de CLIs elegantes com spinners, tabelas e streaming no console. |

### 7.2 Implementação do EventStream em C#

```csharp
namespace OpenHandsNET.Core.Events;

public enum EventSource
{
    User,
    Agent,
    Environment
}

public abstract record BaseEvent
{
    public long Id { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public EventSource Source { get; init; }
    public string ConversationId { get; init; } = string.Empty;
}

public abstract record ActionEvent : BaseEvent;
public abstract record ObservationEvent : BaseEvent;

public record CmdRunAction(string Command, bool KeepPrompt = false) : ActionEvent;
public record CmdOutputObservation(long ActionId, int ExitCode, string Content) : ObservationEvent;

public record FileReadAction(string Path, int? StartLine = null, int? EndLine = null) : ActionEvent;
public record FileReadObservation(string Path, string Content) : ObservationEvent;

public record FileWriteAction(string Path, string Content) : ActionEvent;
public record FileWriteObservation(string Path, bool Success) : ObservationEvent;

public record BrowserAction(string Url, string Command) : ActionEvent;
public record BrowserObservation(string Url, string ScreenshotBase64, string DomContent) : ObservationEvent;

public interface IEventStream
{
    IAsyncEnumerable<BaseEvent> ReadEventsAsync(string conversationId, long startId = 0, CancellationToken ct = default);
    ValueTask PublishAsync(BaseEvent @event, CancellationToken ct = default);
    Task SubscribeAsync(string conversationId, Func<BaseEvent, Task> handler, CancellationToken ct = default);
}
```

### 7.3 Implementação do Agent Loop com Semantic Kernel

```csharp
namespace OpenHandsNET.Core.Agent;

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

public class CodeActAgent
{
    private readonly Kernel _kernel;
    private readonly IEventStream _eventStream;
    private readonly IChatCompletionService _chatService;

    public CodeActAgent(Kernel kernel, IEventStream eventStream)
    {
        _kernel = kernel;
        _eventStream = eventStream;
        _chatService = kernel.GetRequiredService<IChatCompletionService>();
    }

    public async Task StepAsync(string conversationId, ChatHistory history, CancellationToken ct)
    {
        // 1. Obter resposta do modelo
        var response = await _chatService.GetChatMessageContentAsync(history, kernel: _kernel, cancellationToken: ct);
        
        // 2. Analisar tool calls ou comandos de código
        var action = ParseActionFromContent(response);
        await _eventStream.PublishAsync(action with { ConversationId = conversationId }, ct);

        // O runtime processa a action e emite a observation de volta no stream
    }

    private ActionEvent ParseActionFromContent(ChatMessageContent content)
    {
        // Parser de ferramentas ou execução de script bash/csharp
        return new CmdRunAction(content.Content ?? string.Empty);
    }
}
```

---

## 8. Estrutura da Pasta `.specs` para o Repositório

Ao criar as especificações dentro de `afonsoft/OpenHandsNET/.specs/`, a pasta deve conter os seguintes arquivos modulares para guiar desenvolvedores e agentes autônomos:

```
.specs/
├── 01_architecture_overview.md       # Arquitetura de micro-serviços e bibliotecas .NET
├── 02_event_stream_spec.md           # Definição e schemas de todas as Actions e Observations
├── 03_runtime_sandbox_spec.md        # Especificação de orquestração Docker.DotNet e K8s
├── 04_rest_and_signalr_api_spec.md   # Documentação de endpoints REST e SignalR Hubs
├── 05_devin_ui_integration_spec.md   # Especificação visual Devin Web + Open-WebUI
├── 06_cli_spec.md                    # Especificação do OpenHandsNET.CLI em Spectre.Console
├── 07_mcp_integration_spec.md        # Especificação do Model Context Protocol em C#
└── 08_enterprise_security_rbac.md    # Autenticação OIDC, tokens e auditoria
```

---

## 9. Interface Estilo "Devin Web" integrada com Open-WebUI

O Devin Web destaca-se por oferecer uma visão integrada de desenvolvedor ("All-in-one Cockpit"). O **Open-WebUI Net** (`afonsoft/open-webui`) servirá de base para a casca da aplicação (autenticação, listagem de conversas, chat e configurações de modelos), expandindo o painel direito para comportar as ferramentas de execução:

### 9.1 Layout das 4 Janelas (Devin Multi-Pane Workspace)

```
┌────────────────────────────────────────────────────────────────────────────────────────┐
│ OpenHandsNET ─ Navigation Bar (Repo, Branch, Model Selector, Tokens, Status: RUNNING)   │
├──────────────────────┬─────────────────────────────────────────────────────────────────┤
│                      │ [Interactive Terminal]  [Browser Live]  [Editor / Diff]  [MCP]  │
│                      ├─────────────────────────────────────────────────────────────────┤
│                      │                                                                 │
│   CHAT & PLANNER     │ $ dotnet test                                                   │
│   (Open-WebUI Net)   │ Inspecionando projeto...                                        │
│                      │ Total de testes: 42. Aprovados: 42. Falhas: 0.                  │
│  - User Prompt       │                                                                 │
│  - Agent Thoughts    ├─────────────────────────────────────────────────────────────────┤
│  - Step-by-Step Task │ File Tree:                    │ Editor / Diff Preview:          │
│    Checklist         │ ├── src/                      │ @@ -12,4 +12,6 @@               │
│  - Actions Approval  │ │   ├── Program.cs            │ + builder.Host.UseSerilog();    │
│    (Confirm/Reject)  │ │   └── Services/             │                                 │
│                      │ └── tests/                    │                                 │
└──────────────────────┴─────────────────────────────────────────────────────────────────┘
```

### 9.2 Componentes Visuais Necessários
1. **Chat & Thought Stream:** Exibição do raciocínio interno do LLM (`thinking_blocks`), passos planejados (checklist de tarefas com status Pendente/Executando/Concluído) e chat tradicional.
2. **Terminal Virtual Interativo (xterm.js):** Conectado diretamente ao SignalR Hub que envia stdout/stderr do container Docker em tempo real.
3. **Live Browser Preview (Playwright Screencast):** Streaming de screenshots base64 geradas pelo Playwright sempre que o agente executa tarefas de navegação web.
4. **Monaco Editor / Diff Viewer:** Pré-visualização de arquivos alterados antes de comitar ou aplicar patches `git diff`.

---

## 10. Prompt Especializado para o Agente Autônomo / CLI

Este prompt deve ser utilizado pelo Agent CLI (ou Claude Code / OpenHands CLI) ao trabalhar no repositório `afonsoft/OpenHandsNET` sem acesso direto à internet:

```markdown
Você é o Arquiteto e Engenheiro Chefe responsável pela criação e implementação do **OpenHandsNET**, localizado no repositório `afonsoft/OpenHandsNET`.

### Diretrizes de Projeto:
1. **Linguagem & Framework:** C# 13, .NET 9 LTS, ASP.NET Core Minimal APIs, SignalR para comunicação em tempo real.
2. **Arquitetura Base:** Baseada rigorosamente no ecossistema OpenHands (Core, Cloud, Enterprise), utilizando a arquitetura centrada em **EventStream**.
3. **Modelos:**
   - O núcleo de agentes deve usar o **Microsoft Semantic Kernel** com suporte ao protocolo MCP (Model Context Protocol).
   - O runtime de sandbox deve utilizar **Docker.DotNet** para execução em contêineres e **Microsoft.Playwright** para automação web.
4. **Layout & UX:** Inspirado na interface Devin Web e integrado ao ecossistema de interface do **Open-WebUI Net** (`afonsoft/open-webui`), fornecendo terminal xterm.js, navegador em tempo real, visualizador de diff Monaco e chat com rastreamento de pensamento (*Thinking blocks*).
5. **Estrutura de Arquivos:** Siga rigorosamente os artefatos descritos no diretório `.specs/`. Consulte sempre os arquivos `.specs/01_architecture_overview.md` a `.specs/08_enterprise_security_rbac.md`.
6. **Padrões de Código:**
   - Adote Clean Architecture e Domain-Driven Design (DDD).
   - Tipos imutáveis com `record`, `ValueTask`, `IAsyncEnumerable` e `CancellationToken` obrigatório em todos os métodos assíncronos.
   - Testes unitários com xUnit, FluentAssertions e Testcontainers.NET para validar sandboxes Docker reais.

Inicie inspecionando ou criando as especificações na pasta `.specs/` e, em seguida, construa a estrutura de solução `.sln` com os projetos `OpenHandsNET.Core`, `OpenHandsNET.Runtime`, `OpenHandsNET.Web` e `OpenHandsNET.CLI`.
```
