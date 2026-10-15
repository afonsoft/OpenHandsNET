# SPEC — 26 · UI/UX Modernization (Themes, Icons, Chat, Command Surface)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `ui-ux-modernization` |
| Type | `Feature / Client` |
| Stack | `Blazor WASM · Tailwind v4 · Monaco · SignalR` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Done` (PR #73) |
| Ticket | `TBD` |
| Depends on | `05_devin_ui_integration_spec`, `21_client_completion_spec` (Tailwind pipeline + Monaco), `24_agent_activity_protocol_spec` (eventos novos) |
| Reference | `afonsoft/open-webui` ("Open-WebUI+Code" — fork .NET do usuário) |
| Touches | `Client/wwwroot/css`, `Client/Layout`, `Client/Components`, `Client/Services` |

## 1. User Story

**As a** usuário do OpenHandsNET
**I want** tema claro/escuro, ícones consistentes, markdown nas respostas,
cards de tool call enriquecidos, command palette e notificações
**So that** a interface atinge o padrão do Open-WebUI+Code/Devin Web —
hoje: dark-only hardcoded, ícones emoji, tool calls como JSON cru, sem
feedback de conexão.

**Problem context:**

- `app.css`: paleta dark hardcoded — sem tema claro nem `prefers-color-scheme`.
- Ícones são emoji (`🛠️`, etc.) — inconsistentes entre plataformas.
- `ChatPane` renderiza texto plano; respostas com código/tabelas perdem a
  formatação; tool calls mostram JSON bruto.
- Sem feedback de SignalR desconectado; sem command palette; sidebar fixa.
- O fork `afonsoft/open-webui` já tem o vocabulário: `ToolCallCard`,
  `ModelSelector`, `AgentModeToggle`, `AttentionBadge`, `NotificationBell`,
  `ToastHost`, `DialogHost`, `OfflineRetry`, `Sidebar`, IDE workbench.

## 2. Scope (RF)

### RF-001 — Theme system

- CSS vars em `:root`/`[data-theme="light"]` — `app.css` refactor: toda cor
  vira `--ohn-*` (bg, surface, border, text, accent, success, warn, danger,
  code-bg). Dark = paleta atual; light = GitHub light.
- `Services/ThemeService.cs`: `Theme = dark|light|system`; persiste em
  `localStorage("ohn.theme")` + endpoint `PUT /users/me/preferences`
  (novo — `{theme}` mergeado em `UserPreferencesJson` existente, ver
  `UserRecord`); `system` segue `prefers-color-scheme` via JS listener.
- `ThemeInit` inline script no `index.html` (antes do CSS) — zero flash.
- Toggle no header + Settings; `App.razor` aplica `data-theme` no `<html>`.

### RF-002 — Icon system

- `Components/Icon.razor`: `<Icon Name="git" Size=16 />` — paths SVG inline
  (viewBox 24, stroke currentColor), set ~40 ícones (tabler-style, vendored
  em `wwwroot/icons/icons.svg` sprite + fallback inline). Substitui **todos**
  os emoji de chrome (nav, tabs, status, admin). Mapa em `Icons.cs`.
- Emoji de conteúdo (dentro de mensagens do usuário) não muda.

### RF-003 — Chat enriquecido

- `Services/MarkdownService.cs`: Markdig (pipeline avançado — tabelas,
  task-lists, fenced code) → HTML sanitizado (whitelist via
  `HtmlSanitizer` — pacote já auditável; fallback: escape + `<pre>`).
- `EventCard` redesenhado por kind:
  - `action` → `ToolCallCard`: header `icon + tool_name + duração + status
    dot`, args pretty-printed colapsável, link pro `observation`.
  - `command_output`/`tool_progress`/`files_changed`/`run_progress`/
    `work_summary`/`agent_diagnostic` (SPEC-24) → renderers dedicados.
  - `condensation_summary` → card "contexto compactado" expansível.
  - `message` → markdown + bolhas por `source` (user/agent).
- Streaming: cursor `▍` no delta ativo; auto-scroll só quando o usuário está
  no fim (track scroll position — comportamento open-webui).
- `ModelSelector` no header da conversa (`GET /models` SPEC-23 →
  `switch_acp_model`); `AgentModeToggle` (chat/plan/build — plan/build já
  existem como `AgentType`? verificar `ConversationRecord.AgentType`).
- `PermissionPromptCard`/`QuestionPromptCard`: confirmações e perguntas do
  agente viram cards ação (aprova/recusa + reason) — hoje `ConfirmationBanner`
  minimalista.

### RF-004 — Command surface & navegação

- `CommandPalette` (`Ctrl+K`/`/`): fuzzy sobre conversas, páginas admin
  (se super-admin), ações ("nova conversa", "trocar tema", "ir p/ terminal").
  JS mínimo de keyboard; estado via `PaletteService`.
- `Sidebar`: colapsável (`collapsed` em localStorage), seções (Conversas /
  Admin), badge de runs ativas por conversa (`execution_status` hub),
  filtro por texto, item com status dot (running/paused/error) + repo.
- `NotificationBell` + `ToastHost`/`ToastService`: erros de API, eventos
  `agent_diagnostic` severity≥warn, fim de run (quando aba inativa —
  `document.title` badge + Web Notifications opt-in).
- `OfflineRetry`: hub SignalR `Closed` → banner "reconectando…" com
  backoff visual; `event_stream` reconecta com `last_event_id` (já suportado
  pelo endpoint? verificar — senão SPEC define `?after_id=` no subscribe).

### RF-005 — Layout & responsivo

- `MainLayout`: sidebar 260px/64px-collapsed; header com breadcrumbs,
  `ModelSelector`, `ThemeToggle`, `NotificationBell`, avatar menu.
- Mobile (SPEC-15 continuou): drawer sidebar, tabs da conversa em bottom
  sheet, chat full-width, `ConfirmationBanner` fixo bottom.
- `IdeActivityBar`-lite **não** nesta SPEC — workbench IDE completo fica
  para SPEC futura; aqui só tabs atuais ganham polish (empty states com
  ícone+ação, skeletons de loading, error cards com retry).

### RF-006 — Acessibilidade & polish

- Focus rings visíveis, `aria-label` em icon-buttons, roles nos cards.
- `prefers-reduced-motion` desativa transições.
- Contraste AA nos dois temas (verificar pares fg/bg).

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| PUT | `/api/v1/users/me/preferences` | `{theme?}` → merge em `UserRecord.PreferencesJson` |
| GET | `/api/v1/users/me/preferences` | `{theme,…}` |

Sem event kinds novos — consome os da SPEC-24. Migration: coluna
`PreferencesJson` se inexistente (verificar `UserRecord`).

## 4. Data model

`UserRecord.PreferencesJson` (text, default `{}`) — migration se faltar.
Client-side: `localStorage` chaves `ohn.theme`, `ohn.sidebar`.

## 5. NFR / Security

- Markdown **sanitizado server-side-confiável**: `HtmlSanitizer` whitelist
  (sem `script`, `iframe`, `on*`, `javascript:`) — XSS vetor #1 do chat.
- Web Notifications: só após `Notification.requestPermission` explícito.
- Ícones/cores não afetam contratos de evento — puramente client.

## 6. Task Plan

1. Theme: CSS vars refactor + `ThemeService` + init script + endpoints
   preferences + teste bUnit.
2. `Icon`/`Icons.cs` + sprite + sweep de emoji no chrome.
3. Markdown (Markdig + HtmlSanitizer) + `EventCard`/`ToolCallCard` novos +
   renderers SPEC-24 + streaming cursor + auto-scroll inteligente.
4. `ModelSelector`, `PermissionPromptCard`/`QuestionPromptCard`,
   `ConfirmationBanner` enriquecido.
5. `CommandPalette`, `Sidebar` v2, `NotificationBell`/`ToastHost`,
   `OfflineRetry`.
6. Mobile pass (drawer/bottom-sheet) + a11y sweep.
7. bUnit coverage dos componentes novos + `docs/ui/ux.md` (theming, ícones,
   paleta de teclas).

## 7. Deliverables

- Theme system + Icon set + chat rico + command palette + notificações +
  layout responsivo — tudo bUnit-tested; doc `docs/ui/ux.md`.

## 8. Referências

- `afonsoft/open-webui` — componentes vocabulário (`ToolCallCard`,
  `ModelSelector`, `Sidebar`, `NotificationBell`, `OfflineRetry`…):
  https://github.com/afonsoft/open-webui/tree/main/src/OpenWebUI.Client
- Open WebUI upstream (Svelte) — temas/palette (conceitos apenas).
- Tabler Icons (MIT) — geometria dos paths vendored.
- Markdig + HtmlSanitizer — pacotes .NET maduros (>7d, versoes pinadas).

## 9. Guardrails

- Sanitização de HTML obrigatória em todo markdown renderizado.
- Sem CDN — fontes/ícones vendored (airgapped).
- Tema não quebra contratos de evento; preferências nunca vazam secrets.

## 10. DoD

- Toggle dark/light/system sem flash; persistido por usuário.
- Tool calls renderizam como cards expansíveis; markdown com código OK.
- `Ctrl+K` navega; desconexão SignalR mostra banner e reconecta.
- `dotnet test` (client) verde; zero emoji em chrome.
