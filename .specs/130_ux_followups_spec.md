# SPEC — 130 · UX follow-ups (SettingsModal + rail OWUIC completo)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `ux-followups` |
| Type | `Frontend` |
| Status | `Approved` |
| Ticket | #335 (umbrella #329, Epic #317) — débito SPECs 118/119 |
| Scope | `src/OpenHandsNET.Client/` (SettingsPage, DialogHost, MainLayout, sidebar) |

## 1. User Story

**As a** usuário, **I want** acesso a Settings como modal sobre a
conversa e o rail lateral completo do OpenWebUiCode, **so that** o
fluxo não navegue para fora do chat e o shell fique idêntico ao
referencial (Devin Cloud / OWUIC).

## 2. Contexto / escopo

- SPEC-119 entregou `/settings` como **página com rail de abas** +
  SaveBar — funcional, mas OWUIC usa **modal** sobre a conversa.
- SPEC-118 entregou menu ⋯ + bell no desktop; o **rail expandido/
  colapsado** (favicon-only 56px + hover-expand) ficou parcial.

## 3. Requisitos

| ID | Requisito |
|---|---|
| RF-001 | `SettingsModal` — mesmo conteúdo da página atual renderizado via `DialogService` (novo `DialogKind` ou componente dedicado), aberto pelo ícone de engrenagem no rail/sidebar. |
| RF-002 | `/settings` continua acessível por URL direta (deep-link) — modal e página compartilham o mesmo componente de conteúdo. |
| RF-003 | Rail OWUIC: sidebar colapsada mostra favicon + bell (já existe bell); hover-expand ou toggle fixo revela lista de conversas (largura 260px → 56px colapsada). |
| RF-004 | Persistir estado colapsado em `localStorage` (`ohn.sidebar.collapsed`) e respeitar no boot. |
| RF-005 | Focus/aria: modal com focus-trap/Escape (padrão DialogHost); rail com `aria-expanded` + tooltips nos ícones quando colapsado. |
| RF-006 | Mobile: rail colapsado é o default < 768px (drawer existente mantido). |

## 4. Acceptance

- [ ] Engrenagem abre modal com as abas (appearance/agent/secrets/skills) — mesma SaveBar.
- [ ] URL `/settings` renderiza standalone (deep-link não quebra).
- [ ] Toggle colapsa/expande rail; estado persiste entre reloads.
- [ ] bUnit: SettingsModal abre/fecha, rail toggle persiste, focus-trap ativo.
- [ ] `check-css-classes`/`form-a11y` verdes após markup novo.

## 5. API Contract

Sem mudança de API. Novas chaves i18n (`settings.modal.*`, `rail.expand/collapse`)
nos 8 locales — traduções reais (não vazias).

## 6. Task Plan

1. Extrair conteúdo de `SettingsPage` para `SettingsContent` compartilhado.
2. `SettingsModal` via DialogService + trigger no rail/sidebar.
3. Rail collapse/expand + persistência + tooltips.
4. i18n ×8 + bUnit + drift guards + tailwind regen.

## 7. Riscos / notas

- Deep-link /settings deve seguir funcionando (bookmarks).
- DialogHost já provê a11y — reutilizar em vez de modal bespoke.
