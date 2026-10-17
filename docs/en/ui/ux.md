# UI/UX — themes, icons, command surface (SPEC-26)

## Themes

Every colour is a `--ohn-*` CSS custom property defined in
`src/OpenHandsNET.Client/wwwroot/css/app.css`:

| Var | Dark (default) | Light |
|---|---|---|
| `--ohn-bg` / `--ohn-bg-alt` / `--ohn-bg-hover` | `#0d1117` / `#161b22` / `#21262d` | `#ffffff` / `#f6f8fa` / `#eaeef2` |
| `--ohn-border` | `#30363d` | `#d0d7de` |
| `--ohn-fg` / `--ohn-fg-dim` | `#c9d1d9` / `#8b949e` | `#1f2328` / `#59636e` |
| `--ohn-accent` | `#58a6ff` | `#0969da` |
| `--ohn-green`/`--ohn-yellow`/`--ohn-red`/`--ohn-purple` | GitHub dark | GitHub light |

**Resolution:** `ThemeService` (`dark|light|system`) → `data-theme` on
`<html>`. `system` follows `prefers-color-scheme` with a JS listener
(`OnSystemThemeChanged`).

**Persistence:** `localStorage("ohn.theme")` + merge into
`PUT /api/v1/users/me/preferences` → `UserRecord.PreferencesJson`. The
inline `ThemeInit` in `index.html` applies the theme **before** CSS —
zero flash.

**Toggle:** sidebar footer (`ThemeToggle` cycles dark→light→system)
and a select under `/settings` → Appearance.

## Icons

`<Icon Name="…" Size="16" />` — vendored inline SVG
(`Components/Icons.cs`, ~40 Tabler-style paths, MIT,
`stroke="currentColor"`). No emoji in chrome (nav, status, card
headers); user content emoji stays free.

## Markdown

`MarkdownService.Render()` — Markdig (tables, task-lists, fenced code)
→ `HtmlSanitizer` whitelist (no `script`, `iframe`, `on*`,
`javascript:`). All rendered markdown goes through sanitize — XSS is
the #1 chat vector.

## Command surface

- **Command palette**: `Ctrl+K` or `/` (outside inputs) → fuzzy over
  actions + recent conversations. `PaletteService` + `ohnKeys` in
  `theme.js`.
- **Sidebar v2**: 260px (64px collapsed, `localStorage "ohn.sidebar"`),
  Conversations/General/Admin sections, per-conversation status dots,
  text filter. Mobile: off-canvas drawer.
- **Toasts**: `ToastService` (info/success/warn/error, cap 5,
  auto-dismiss). `agent_diagnostic` severity ≥ warn and run errors
  notify automatically.
