# SPEC — 05 · Devin-Style UI (Blazor WASM)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `devin-ui-integration` |
| Type | `Frontend / Feature` |
| Stack | `Blazor WASM · Tailwind v4 · SignalR client · xterm.js · Monaco` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` |
| Ticket | [#6](https://github.com/afonsoft/OpenHandsNET/issues/6) (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `04_rest_and_signalr_api_spec` |
| Reuses | `afonsoft/open-webui` (Client: chat, layout, settings, auth, i18n) |

## 1. User Story

**As a** usuário do OpenHandsNET
**I want** um cockpit de desenvolvedor estilo Devin Web — chat/planner à esquerda,
terminal+browser+editor à direita
**So that** eu acompanhe o agente trabalhar, aprove ações e inspecione o workspace
num só lugar — a experiência que diferencia o produto de um chat comum.

**Problem context:** o frontend upstream (Agent Canvas, `OpenHands/src/routes/`)
já tem esse layout: `conversation.tsx` + tabs `planner-tab`, `task-list-tab`,
`browser-tab`, `files-tab`, `commits-tab`, `usage-tab`. O `open-webui-net` Client
fornece chat/settings/auth/layout prontos em Blazor — o OpenHandsNET estende esse
shell com os painéis de execução.

## 2. Scope

**In scope:**
- Shell: lista de conversas, settings (LLM, API keys, MCP, secrets), auth — herdados do open-webui-net.
- `ConversationPage` multi-pane:
  - **Esquerda:** chat (mensagens + `thinking`/ActionEvent renderizados), planner (checklist `TaskTracker`), aprovações (`waiting_for_confirmation` → Confirm/Reject).
  - **Direita (tabs):** Terminal (xterm.js live), Browser (screencast Playwright), Files (tree + viewer), Editor/Diff (Monaco diff de `git/changes`), Commits, Usage (tokens/custo).
- Home (`/`): nova conversa com `initial_message` + `selected_repository`, lista com `sandbox_status`/`execution_status`.
- Estado realtime via `ConversationHub` (SPEC 04).
- `SharedConversationPage` (view-only, link público).

**Out of scope:** automações UI, extensions hub, VS Code embutido real (Monaco
read-only/diff no MVP), mobile app (WASM responsivo basta no MVP), i18n completo
(en+pt-BR no MVP — open-webui-net já tem base).

## 3. Technical Context

**Upstream:** `src/routes/conversation.tsx`, `planner-tab.tsx`, `task-list-tab.tsx`,
`browser-tab.tsx`, `files-tab.tsx`, `commits-tab.tsx`, `usage-tab.tsx`,
`shared-conversation.tsx`, `home.tsx`, `settings*.tsx` (agent/llm/mcp/secrets/
condenser/verification). Data layer: TanStack Query → API client → hooks.
**Equivalente Blazor:** componentes `.razor` + `ConversationStateService`
(singleton por página, fonte única de verdade reativa) + HttpClient tipado.
**Where:** `Client/Pages/`, `Client/Components/Conversation/`, `Client/Services/`,
`Client/wwwroot/js/` (xterm.js, monaco interop).

## 4. Requirements

### RF-001: Layout multi-pane
```
┌──────────────────────────────────────────────────────────────────┐
│ TopBar: repo · branch · model selector · tokens · ● RUNNING      │
├───────────────────┬──────────────────────────────────────────────┤
│ Chat & Planner    │ Tabs: Terminal | Browser | Files | Diff |…   │
│ (coluna fixa)     │                                              │
└───────────────────┴──────────────────────────────────────────────┘
```
Panes redimensionáveis; estado persistido em `localStorage` por conversa.

### RF-002: Chat & event stream
- Renderiza `MessageEvent` (chat), `ActionEvent` (cards por tool: bash/file/browse),
  `ObservationEvent` (output colapsável), `TokenEvent`/`StreamingDeltaEvent`
  (resposta parcial), `Condensation*` (marcador), `AgentErrorEvent`/`ServerErrorEvent` (alertas).
- `ConversationStateUpdateEvent` atualiza badges: `sandbox_status`, `execution_status`, tokens, step atual.

### RF-003: Planner/Task list
- Checklist derivado de `TaskTracker` actions: item `pending→in_progress→done`;
  seção "Plan" quando o agente usa modo plan (alinhado ao SPEC `agent-modes-plan-build` do open-webui-net).

### RF-004: Confirmation mode
- `execution_status=waiting_for_confirmation` → banner com ação pendente +
  botões Approve/Reject → `ConfirmAction` no hub; `UserRejectObservation` renderizado.

### RF-005: Terminal tab
- xterm.js via JS interop; recebe `terminal_out` (stdout/stderr); `terminal_in`
  opcional quando `interactive_terminal=true` na conversa.

### RF-006: Browser tab
- Último screenshot (`BrowserObservation.screenshot_base64`) + URL/DOM resumo;
  histórico como filmstrip clicável.

### RF-007: Files + Diff + Commits
- `GET /{id}/files` (tree lazy) → `GET /{id}/file?path=` (Monaco read-only);
- `GET /{id}/git/changes` lista alterados; `GET /{id}/git/diff` → Monaco diff side-by-side;
- `commits-tab`: log de commits criados pelo agente.

### RF-008: Start flow UX
- `POST /app-conversations` → **progress UI do start-task** (`WORKING→…→READY`,
  ou SSE `stream-start`) → navega a `/conversations/{id}` quando `READY`.

**Invariants:** um writer por estado (`ConversationStateService`); componentes
→ service → HttpClient/Hub (nunca chamam API direto — regra equivalente à
TanStack do upstream); polling só como fallback do hub.

## 5. API Contract

Consome SPEC 04 inteiro + hub `/hubs/conversations`. Nenhum endpoint novo.

## 6. Critérios de Aceite

- [ ] Criar conversa mostra progresso do start-task e abre a conversa ao `READY`.
- [ ] Mensagem enviada aparece como evento; ações/observações renderizam em tempo real via hub.
- [ ] Banner de confirmação aprova/rejeita e desbloqueia o agente.
- [ ] Terminal exibe saída do bash em <500ms; Files/Diff refletem alterações reais do workspace.
- [ ] Lista de conversas com status; link de compartilhamento view-only funciona.
- [ ] bUnit: componentes-chave renderizam estados (running/paused/confirmation/error).

## 7. Task Plan

1. Fork/reuse do shell open-webui-net Client + `ConversationStateService` + typed `HttpClient` (verify: home lista conversas).
2. `ConversationPage` layout + chat event rendering (verify: bUnit + E2E mock).
3. Tabs Terminal (xterm interop) + Browser (screenshot) (verify: E2E).
4. Files/Diff/Commits (Monaco interop) (verify: E2E).
5. Planner + confirmation banner (verify: E2E confirmation flow).
6. Start-task progress + shared view + polish Lighthouse≥90 (verify: audit).

## 8. Deliverables — arquivos a criar/modificar

```text
src/OpenHandsNET.Client/Pages/Home.razor                   # lista + nova conversa
src/OpenHandsNET.Client/Pages/ConversationPage.razor       # shell multi-pane
src/OpenHandsNET.Client/Pages/SharedConversationPage.razor # view-only
src/OpenHandsNET.Client/Pages/Settings/{LlmSettings,McpSettings,
  SecretsSettings,AgentSettings}Page.razor                 # portar de open-webui-net
src/OpenHandsNET.Client/Components/Conversation/ChatPane.razor
src/OpenHandsNET.Client/Components/Conversation/EventCard.razor        # por kind
src/OpenHandsNET.Client/Components/Conversation/PlannerPane.razor
src/OpenHandsNET.Client/Components/Conversation/ConfirmationBanner.razor
src/OpenHandsNET.Client/Components/Conversation/TerminalTab.razor      # xterm
src/OpenHandsNET.Client/Components/Conversation/BrowserTab.razor       # screenshots
src/OpenHandsNET.Client/Components/Conversation/FilesTab.razor         # tree+viewer
src/OpenHandsNET.Client/Components/Conversation/DiffTab.razor          # Monaco diff
src/OpenHandsNET.Client/Components/Conversation/CommitsTab.razor
src/OpenHandsNET.Client/Components/Conversation/UsageTab.razor
src/OpenHandsNET.Client/Services/ConversationStateService.cs # fonte única reativa
src/OpenHandsNET.Client/Services/OpenHandsNetApiClient.cs    # typed HttpClient
src/OpenHandsNET.Client/Services/ConversationHubClient.cs    # SignalR client
src/OpenHandsNET.Client/wwwroot/js/{xtermInterop,monacoInterop,splitPane}.js
src/OpenHandsNET.Client/tailwind.input.css                   # gerar c/ tailwindcss CLI
tests/OpenHandsNET.Client.Tests/                            # bUnit
```

## 9. Referências e links

### Internas (repo)
- `docs/specs/04_rest_and_signalr_api_spec.md` — contratos REST+hub consumidos.
- `docs/specs/02_event_stream_spec.md` — kinds renderizados por `EventCard`.

### UI upstream a espelhar (Agent Canvas, baseline `d7e14e2`, MIT)
- Página da conversa: https://github.com/OpenHands/OpenHands/blob/d7e14e2/src/routes/conversation.tsx
- Tabs: …/src/routes/{planner-tab,task-list-tab,browser-tab,files-tab,commits-tab,usage-tab}.tsx
- Home/nova conversa: …/src/routes/home.tsx · compartilhada: …/src/routes/shared-conversation.tsx
- Settings: …/src/routes/{llm-settings,mcp-settings,agent-settings,condenser-settings}.tsx
- Componentes de conversa: https://github.com/OpenHands/OpenHands/tree/d7e14e2/src/components/features/conversation/

### Base Blazor a reusar (open-webui-net, baseline `5a7d920`)
- Páginas: https://github.com/afonsoft/open-webui/tree/5a7d920/src/OpenWebUI.Client/Pages
  (`ChatPage.razor`, `Ide.razor`, `TerminalPage.razor`, `Workspace.razor`, `SharedChat.razor`)
- Componentes: …/Components (`ChatView.razor`, `ChatWorkspacePanel.razor`, `Ide*`, `Terminal*`)
- Layout: …/Layout/MainLayout.razor · Services: …/Services/
- `tailwind.input.css`: …/src/OpenWebUI.Client/tailwind.input.css

### Bibliotecas / framework
- Blazor WASM: https://learn.microsoft.com/aspnet/core/blazor/hosting-models#blazor-webassembly
- JS interop: https://learn.microsoft.com/aspnet/core/blazor/javascript-interoperability/call-javascript-from-dotnet
- SignalR client (.NET + JS): https://learn.microsoft.com/aspnet/core/signalr/dotnet-client
- xterm.js: https://xtermjs.org/ (+ addon-fit)
- Monaco Editor: https://microsoft.github.io/monaco-editor/ (diff editor API)
- Tailwind v4: https://tailwindcss.com/docs/installation (CLI: `tailwindcss -i … -o …`)
- bUnit: https://bunit.dev/ · Playwright .NET E2E: https://playwright.dev/dotnet/

## 10. Guardrails de execução (Devin)

- Componentes **nunca** chamam `HttpClient`/hub direto — sempre via
  `ConversationStateService`/`OpenHandsNetApiClient` (regra upstream: hooks→DAL).
- Um writer por estado: `ConversationStateService` é a única fonte de verdade da página.
- CSS: editar `tailwind.input.css` e regerar `wwwroot/css/tailwind.css` (artefato
  commitado — não editar a saída à mão).
- i18n mínimo en+pt-BR; strings em resource/JSON (não hardcode).
- Acessibilidade: tabs com `role=tablist`, navegação por teclado, foco visível.
- E2E Devin: mock hub/API primeiro (`[Category("E2E")]` opt-in); não depender de LLM real.

## 11. Definition of Done

- [ ] Todos os RFs (§4) e critérios (§6) verificados em E2E/bUnit.
- [ ] Multi-pane funcional: chat + terminal + browser + files + diff + commits + usage.
- [ ] Confirmation flow aprova/rejeita em tempo real; backlog recupera após F5.
- [ ] Lighthouse ≥ 90 em home + conversa; sem erros de console.
- [ ] `dotnet test` + `dotnet build` verdes; SPEC `Done`; issue fechada pelo PR.
