# SPEC — 116 · UI design-system parity (tokens + component classes + themes)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `ui-design-system-parity` |
| Type | `Frontend` |
| Status | `In implementation` |
| Ticket | #318 (Epic #317) — gap-analysis OpenWebUiCode 2026-11-04 |
| Scope | `src/OpenHandsNET.Client/` (Styles, wwwroot/css, index.html, componentes .razor) + `tools/` (baselines) |

## 1. User Story

**As a** usuário do OpenHandsNET, **I want** a superfície visual com o
mesmo design-system do OpenWebUiCode (tokens oklch, classes
componentes compartilhadas, tipografia Inter, micro-interações),
**so that** o produto tenha o acabamento visual do Open WebUI upstream
e do Devin Cloud — e futuras telas reutilizem classes em vez de CSS
bespoke por componente.

## 2. Contexto / escopo

Hoje (AS-IS, evidência):

- `wwwroot/css/app.css` (957 linhas) é hand-written com tokens
  `--ohn-*` hex; `Styles/app.tailwind.css` só emite utilidades
  (`wwwroot/tailwind.css` = 22 KB vs 173 KB no OWUIC). Sem `@theme`,
  escala gray termina em tons padrão — sem `gray-750/850/925/950`
  usados pelo OWUIC para surfaces escuras intermediárias.
- OWUIC `tailwind.input.css` (876 linhas) provê biblioteca de classes
  componentes: `.page`, `.page-header/-title/-new/-tabs`, `.card-*`,
  `.menu/-item/-sep`, `.modal-*`, `.modal-tabs` rail, `.input`,
  `select.input` (chevron SVG próprio), `.check`, `.toggle`,
  `.primary-btn`, `.button-sm`, `.auth-submit`, `.danger`, `.icon-btn`,
  `.icon-mini`, `.msg-action`, `.message-*`, `.codeblock-*`,
  `.settings-section*`, `.save-bar*`, `.markdown-body` (typography
  plugin), `.mention-*`, `.test-*`, `.chat-*`, `.shared-*`,
  `.role-badge`, `.field*`, `.memory-*`, `.skip-link`, `.boot-splash`,
  `.typing`, `.thinking-brain/-dots`, `.avatar-ping`, `.msg-cv`.
- OWUIC tema: `@custom-variant dark (&:where(.dark, .dark *))` —
  classe `.dark` no `<html>` + `data-theme`; boot repinta paleta
  custom (`webui.theme.id`/`.css`, `themes/builtin.json`,
  `theme-<id>`). OHN usa só `[data-theme]` — manter atributo e mapear.
- Faltam no OHN: scrollbar fina global (5 px) + utility
  `scrollbar-hidden`; `content-visibility` por mensagem; codeblock
  toolbar injetada por `codeblocks.js` (copiar/linguagem/wrap);
  tipografia plugin `@tailwindcss/typography` (`prose`); foco
  `:focus-visible` com anel duplo (light+dark) — OHN tem outline
  simples; boot-splash com logo circular (vs `loading-progress` svg);
  temas custom de usuário.
- Já coberto (não refazer): Inter var (`fonts/inter-var-*.woff2` +
  `@font-face`), i18n 8 locales, skip-link, reduced-motion reset
  (`app.css:590`), tokens light/dark base (SPEC-114).

Direção: portar a camada de design do OWUIC adaptando nomes —
manter tokens `--ohn-*` como aliases semânticos ou migrar markup para
as classes novas (decisão por componente, documentada na tarefa);
Tailwind segue gerado (`tailwindcss -i … -o wwwroot/tailwind.css`),
nunca editado à mão.

Fora de escopo: redesign de páginas inteiras (SPEC-117+ tratam shell,
dialogs e chat UX); novos endpoints; mudança de rotas.

## 3. Requisitos

- RF-001 — **Tema Tailwind**: portar `@theme` do OWUIC — escala
  `--color-gray-50…950` oklch incluindo `750/850/925`, `--font-sans`
  Inter Variable + `--font-mono`; `@custom-variant dark` mapeando para
  `[data-theme="dark"]` **e** `.dark` (compat com markup legado e
  futuro). Bridge: variáveis `--ohn-*` passam a referenciar os novos
  tokens (sem regressão visual nos componentes atuais).
- RF-002 — **Biblioteca de classes componentes**: portar o conjunto
  acima para `Styles/app.tailwind.css` (`@layer components`), com nomes
  idênticos ao OWUIC onde a semântica for igual (facilita portar
  `.razor` futuros). Classes sem consumidor ficam fora do baseline.
- RF-003 — **Tipografia/markdown**: adicionar `@tailwindcss/typography`
  e `.markdown-body` (`prose prose-sm dark:prose-invert`); aplicar no
  render de mensagens/eventos markdown existentes.
- RF-004 — **Scrollbars**: regra global 5 px (`::-webkit-scrollbar`
  + `scrollbar-color`) + `@utility scrollbar-hidden` — paridade upstream.
- RF-005 — **Foco**: anel `:focus-visible` duplo (light: branco+gray-500;
  dark: gray-950+gray-400) no conjunto interativo do OWUIC; auditar e
  remover `outline-none` sem substituto (drift `check-form-a11y` +
  findings A1 do design-review OWUIC).
- RF-006 — **Micro-interações**: `.typing`, `.thinking-brain`,
  `.thinking-dots`, `.avatar-ping`, `.msg-cv` (content-visibility
  + contain-intrinsic-size nas listas de eventos/mensagens longas);
  respeitar reduced-motion já existente.
- RF-007 — **Codeblock toolbar**: portar `codeblocks.js` (wrap/expand/
  copy + barra linguagem) e classes `.codeblock-*`; integrar ao
  `MarkdownService`/render atual.
- RF-008 — **Boot + tema boot**: `.boot-splash` (logo + nome) no
  `index.html`; `theme-color` meta; preload da fonte Inter;
  `document.documentElement.dataset.theme` + classe `.dark` escritos
  juntos no boot inline (paridade com RF-001).
- RF-009 — **Temas custom de usuário**: `themes/builtin.json` +
  leitura de `ohn.theme.id`/`.css` no boot (injeta `<style
  id="user-theme">` + `theme-<id>`), UI mínima em Settings (select de
  tema builtin; CSS custom = P2 com editor). Seguir o modelo do
  `themes/builtin.json` do OWUIC.
- RF-010 — **Baselines/docs**: regenerar `tools/drift-baseline` apenas
  p/ classes novas intencionais; atualizar `docs/` (tokens, classes,
  theming) e i18n (novas chaves → 8 locales, ratchet ativo).

## 4. Acceptance

- [ ] `tailwind.css` gerado contém o `@theme` completo e as classes
      portadas; `check-css-classes.py` sem violações novas.
- [ ] Paridade visual: sidebar/conversation/settings renderizam com
      tokens oklch; dark/light/system sem regressão (screenshots).
- [ ] Lista de eventos longa usa `msg-cv`; scrollbars finas visíveis
      no Chromium/Firefox; `scrollbar-hidden` aplicada onde o OWUIC usa.
- [ ] Codeblocks renderizados exibem barra (linguagem + copiar + wrap)
      e copia funciona.
- [ ] Boot sem flash de tema errado; `.dark` + `data-theme` coerentes;
      tema custom builtin selecionável e persiste reload.
- [ ] `dotnet build` 0 warnings; `dotnet test` verde; drift guards
      (css-classes, form-a11y, i18n parity/placeholders) passando.
- [ ] Nenhum contrato de API alterado.

## 5. API Contract

Sem mudanças. Preferência de tema continua em
`PUT /users/me/preferences` (`theme` ∈ `dark|light|system`); o id de
tema custom fica client-side (localStorage) nesta fase.

## 6. Task Plan

1. `@theme` + variante dark + bridge `--ohn-*` → build + teste visual.
2. Biblioteca de classes componentes (blocos: form/menu/modal/card/
   message/settings) + tipografia plugin.
3. Scrollbars + foco + micro-interações + msg-cv.
4. `codeblocks.js` + `.codeblock-*`.
5. Boot-splash + index.html (meta/theme-color/preload/.dark).
6. `themes/builtin.json` + select de tema + injeção boot.
7. Baselines + i18n + docs + screenshots.

## 7. Riscos / notas

- Dupla fonte de verdade (`--ohn-*` vs classes novas): o bridge de
  RF-001 precisa de diff visual por página; regressões caem no
  `check-css-classes` (classe definida sem uso) — tratar no baseline.
- Licença: classes/markup são port do fork MIT do próprio grupo
  (`afonsoft/open-webui` → OpenWebUiCode) — compatível; não copiar
  nada do upstream Svelte além de conceitos.
- `@source` do tailwind deve cobrir `wwwroot/js/*.js` (codeblocks
  injeta classes via JS) — senão o purge remove `.codeblock-*`.
