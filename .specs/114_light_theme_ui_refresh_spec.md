# SPEC — 114 · Light theme default + UI/UX refresh (Open-WebUI/Devin look)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `light-theme-ui-refresh` |
| Type | `Feature` |
| Status | `Draft` |
| Ticket | pedido direto do usuário (2026-10-26): "default tema claro, melhorar fonte/layout/usabilidade, parecer Open-WebUI/Devin Web" |
| Scope | `src/OpenHandsNET.Client/` (css, layout, componentes, index.html) + `tests/OpenHandsNET.Client.Tests` |

## 1. User Story

**As a** usuário do OpenHandsNET, **I want** o app abrir em tema claro
por padrão com tipografia e layout polidos próximos do Open-WebUI/Devin
Web, **so that** a experiência pareça um produto acabado e consistente
com o restante do ecossistema (open-webui fork, LLMRouter).

## 2. Contexto / escopo

Hoje:

- `index.html`/`ThemeService` default = **dark** (GitHub dark palette);
  `color-scheme` meta = dark; sem localStorage → dark sempre.
- `app.css` usa stack de fonte genérica do sistema, font-size 14px,
  componentes utilitários mínimos (botões/inputs ~6px radius).
- Layout: `MainLayout` + páginas funcionais, mas sem o polimento visual
  do Open-WebUI (espaçamento, hierarquia, radius, sombras sutis, empty
  states, sidebar).

Direção (Open-WebUI/Devin como referência visual, não cópia de código):

- **Default light**: `ThemeService`/`index.html`/`color-scheme` →
  `light` quando não há preferência persistida. Opção `system` segue
  `prefers-color-scheme` (já existe). Preferência do usuário continua
  vencendo (localStorage + `PUT /users/me/preferences`).
- **Tipografia**: stack "Inter-first"
  (`Inter, -apple-system, 'Segoe UI', Roboto, …`) com `@font-face`
  self-hosted ou fallback sistema; mono mantém `Cascadia Code` primeiro.
  Escala tipográfica consistente (títulos 18–24px semibold, corpo 14px,
  meta 12px) e `line-height` confortável (1.5).
- **Tokens**: paleta light refinada — bg `#ffffff`, bg-alt `#f9fafb`
  (sidebar/panels), border `#e5e7eb`, fg `#111827`, fg-dim `#6b7280`,
  accent azul `#2563eb`; radius maior em cards/modais (8–12px), sombras
  `sm`/`md` sutis em surfaces elevadas. Dark palette permanece
  disponível (toggle já existe), ajustada para contraste equivalente.
- **Usabilidade**: espaçamento lateral consistente nas páginas
  (max-width em settings/admin), empty-states com ícone+copy+ação,
  focus-visible ring consistente, hover states, densidade de tabela,
  e polimento do `MainLayout` (sidebar leve em light, logo/título,
  separadores). Sem mudança de rotas/contratos.

Fora de escopo: redesign de páginas inteiras, i18n de novos textos
além das chaves necessárias, migração para Tailwind utility-first nos
componentes (css custom com vars `--ohn-*` permanece — drift guard
`check-css-classes` segue valendo).

## 3. Requisitos

- RF-001: default light — `index.html` (`color-scheme`, ThemeInit
  fallback), `ThemeService` (Theme/EffectiveTheme defaults) → `light`.
  Persistência continua vencendo; `system` resolve pelo media query.
- RF-002: tipografia — fonte Inter com fallback de sistema; carregar
  self-hosted (woff2 em `wwwroot/fonts/` + `@font-face` com
  `font-display: swap`) **ou** documentar fallback se fonte não for
  commitada (preferir self-hosting para look consistente; licença OFL).
- RF-003: tokens light/dark refinados + novos tokens de elevação
  (`--ohn-shadow-sm/md`, radius `--ohn-radius-md/lg`) aplicados nos
  componentes principais (cards, modais, sidebar, composer, message
  bubbles, tabelas, tabs).
- RF-004: usabilidade — focus-visible ring (accent) em todos os
  interativos; empty-states padronizados; espaçamento/max-width das
  páginas de settings; ThemeToggle acessível (aria-pressed/label i18n).
- RF-005: testes — bUnit para ThemeService default light (via stub JS)
  e ThemeToggle; atualizar baselines de drift (`check-css-classes`,
  `check-i18n-parity`) se novas classes/chaves entrarem.
- RF-006: docs — `docs/` atualizado (theme default light, variáveis);
  README screenshots atualizados se trivial.

## 4. Acceptance

- [ ] Cold boot sem localStorage renderiza light (verificado em browser
      real, screenshot anexado); toggle dark/light/system funciona e
      persiste.
- [ ] UI com look consistente com open-webui fork em light (sidebar
      clara, cards com radius/sombra sutil, tipografia Inter).
- [ ] `dotnet build` 0 warnings; `dotnet test` verde; drift guards
      (`check-css-classes.py`, `check-form-a11y.py`, `check-i18n-parity.py`,
      `check-i18n-placeholders.py`) passando — baseline regenerada se
      mudança de classes for intencional.
- [ ] Nenhum contrato de API alterado.

## 5. API Contract

Sem mudanças de contrato. `PUT /users/me/preferences` já aceita
`theme` (valores `dark`/`light`/`system` — inalterado).

## 6. Riscos / notas

- Flash de tema: ThemeInit inline continua necessário — default muda,
  mecanismo não.
- i18n: novas chaves precisam paridade nos 8 locales (ratchet ativo).
- Baselines de drift: regenerar com `--write-baseline` apenas para
  drift novo intencional; nunca apagar entradas existentes sem resolver.
