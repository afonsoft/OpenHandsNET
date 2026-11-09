# UI/UX — temas, ícones, command surface (SPEC-26)

## Temas

Toda cor é uma CSS custom property `--sc-*` definida em
`src/Sandcastle.Client/wwwroot/css/app.css`:

| Var | Dark (default) | Light |
|---|---|---|
| `--sc-bg` / `--sc-bg-alt` / `--sc-bg-hover` | `#0d1117` / `#161b22` / `#21262d` | `#ffffff` / `#f6f8fa` / `#eaeef2` |
| `--sc-border` | `#30363d` | `#d0d7de` |
| `--sc-fg` / `--sc-fg-dim` | `#c9d1d9` / `#8b949e` | `#1f2328` / `#59636e` |
| `--sc-accent` | `#58a6ff` | `#0969da` |
| `--sc-green`/`--sc-yellow`/`--sc-red`/`--sc-purple` | GitHub dark | GitHub light |
| `--sc-code-bg`, `--sc-msg-user-bg`, `--sc-error-ui-*` | — | — |

**Resolução:** `ThemeService` (`dark|light|system`) →
`data-theme` no `<html>`. `system` segue `prefers-color-scheme` com
listener JS (`OnSystemThemeChanged`).

**Persistência:** `localStorage("sc.theme")` + merge em
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
  ações + conversas recentes. `PaletteService` + `scKeys` em `theme.js`.
- **Sidebar v2**: 260px (64px colapsada, `localStorage "sc.sidebar"`),
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
- Auto-scroll só quando o usuário está no fim (`scChat.pin`).

## Acessibilidade

`:focus-visible` com anel `--sc-accent`, `aria-label` em todos os
icon-buttons, `role="listbox"/"alert"/"alertdialog"` nos componentes
interativos, `prefers-reduced-motion` zera transições, pares fg/bg
contraste AA nos dois temas.
