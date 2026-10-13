# SPEC — 05 · Devin-Style UI (Blazor WASM)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `devin-ui-integration` |
| Type | `Frontend / Feature` |
| Stack | `Blazor WASM · Tailwind v4 · SignalR client · xterm.js · Monaco` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
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
