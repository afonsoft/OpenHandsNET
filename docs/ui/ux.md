# UI/UX — temas, ícones, command surface (SPEC-26)

## Temas

Toda cor é uma CSS custom property `--ohn-*` definida em
`src/OpenHandsNET.Client/wwwroot/css/app.css`:

| Var | Dark (default) | Light |
|---|---|---|
| `--ohn-bg` / `--ohn-bg-alt` / `--ohn-bg-hover` | `#0d1117` / `#161b22` / `#21262d` | `#ffffff` / `#f6f8fa` / `#eaeef2` |
| `--ohn-border` | `#30363d` | `#d0d7de` |
| `--ohn-fg` / `--ohn-fg-dim` | `#c9d1d9` / `#8b949e` | `#1f2328` / `#59636e` |
| `--ohn-accent` | `#58a6ff` | `#0969da` |
| `--ohn-green`/`--ohn-yellow`/`--ohn-red`/`--ohn-purple` | GitHub dark | GitHub light |
| `--ohn-code-bg`, `--ohn-msg-user-bg`, `--ohn-error-ui-*` | — | — |

**Resolução:** `ThemeService` (`dark|light|system`) →
`data-theme` no `<html>`. `system` segue `prefers-color-scheme` com
listener JS (`OnSystemThemeChanged`).

**Persistência:** `localStorage("ohn.theme")` + merge em
`PUT /api/v1/users/me/preferences` → `UserRecord.PreferencesJson`.
O `ThemeInit` inline no `index.html` aplica o tema **antes** do CSS —
zero flash.

**Toggle:** footer da sidebar (`ThemeToggle` cicla dark→light→system) e
select em `/settings` → Appearance.

## Ícones

`<Icon Name="…" Size="16" />` — SVG inline vendored (`Components/Icons.cs`,
~40 paths estilo Tabler, MIT, `stroke="currentColor"`). Proibido emoji em
chrome (nav, status, headers de cards); emoji de conteúdo do usuário segue
livre.

## Markdown

`MarkdownService.Render()` — Markdig (tabelas, task-lists, fenced code) →
`HtmlSanitizer` whitelist (sem `script`, `iframe`, `on*`, `javascript:`).
Todo markdown renderizado passa por sanitize — XSS é o vetor #1 do chat.

## Command surface

- **Command palette**: `Ctrl+K` ou `/` (fora de inputs) → fuzzy sobre
  ações + conversas recentes. `PaletteService` + `ohnKeys` em `theme.js`.
- **Sidebar v2**: 260px (64px colapsada, `localStorage "ohn.sidebar"`),
  seções Conversas/Geral/Admin, dots de status por conversa, filtro
  textual. Mobile: vira drawer off-canvas.
- **Toasts**: `ToastService` (info/success/warn/error, cap 5, auto-dismiss).
  `agent_diagnostic` severity ≥ warn e erros de run notificam
  automaticamente.
- **Offline**: `OfflineBanner` aparece em `Reconnecting`/`Closed` do hub
  SignalR (`WithAutomaticReconnect` já reconecta; o evento
  `ConnectionChanged` alimenta o banner).

## Chat

- Mensagens (`MessageEvent`) renderizam markdown em bolhas user/agent.
- `ActionEvent` → card de tool call: ícone por tool, args JSON
  colapsáveis, comando inline para bash.
- Deltas efêmeros (SPEC-34) mostram cursor `▍` piscante.
- Auto-scroll só quando o usuário está no fim (`ohnChat.pin`).

## Acessibilidade

`:focus-visible` com anel `--ohn-accent`, `aria-label` em todos os
icon-buttons, `role="listbox"/"alert"/"alertdialog"` nos componentes
interativos, `prefers-reduced-motion` zera transições, pares fg/bg
contraste AA nos dois temas.
