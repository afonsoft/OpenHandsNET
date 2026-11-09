# SPEC — 136 · Beta nav & visual polish (nav órfã + font preload + ícones)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `beta-nav-visual-polish` |
| Type | `Frontend` |
| Stack | `Blazor WASM · Tailwind v4` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | #370 · Epic #365 |
| Scope | `src/OpenHandsNET.Client/` + `tests/` |

## 1. User Story

**As a** usuário, **I want** que todas as páginas existentes sejam
alcançáveis pelo menu, que a fonte carregue sem flash e que os ícones
necessários ao produto existam no dicionário, **so that** a navegação
pareça completa e o primeiro paint seja limpo — paridade com o shell do
OpenWebUiCode.

## 2. Contexto / escopo

Achados da auditoria visual (evidência):

- **Páginas órfãs**: `/knowledge` (`Pages/KnowledgePage.razor`),
  `/settings/skills` e `/settings/mcp` existem e rodam, mas nenhum
  item da sidebar/menu leva a elas — só URL direta ou link solto dentro
  do Settings.
- **Fonte**: `inter-var-latin.woff2` + `inter-var-latin-ext.woff2` são
  usadas mas **sem `<link rel="preload">`** no `index.html` → FOUT no
  primeiro paint (OWUIC preloada a Inter em `index.html:13`). Há ainda
  declaração duplicada de família: `'Inter Variable'`
  (app.tailwind.css) e `'Inter'` (app.css unicode-range split) — o
  `--ohn-sans` usa `'Inter'`.
- **Ícones**: `Components/Icons.cs` tem 47 nomes; faltam os que o
  roadmap do menu/ações precisa — `pin`, `archive`, `pencil`/`edit`,
  `share-nodes`, `branch`, `thumbs-up`, `thumbs-down`, `sparkles`,
  `globe`, `tag`, `mic`, `sliders`, `folder`, `link-external`.
- **Seções da sidebar**: hoje Conversations / General / Admin. Faltam
  entradas para Knowledge e para os sub-ajustes — padrão OWUIC de
  footer nav (Workspace/Notes/etc.) não se aplica 1:1, mas as páginas
  existentes precisam de entrada.

Decisões de design:

- **Seção "Workspace"** na sidebar entre Conversations e General:
  Knowledge (`/knowledge`, ícone `book`/`globe`), e sob Settings (modal
  ou página) os links de Skills e MCP já existentes — promovê-los a
  itens de menu quando rotas próprias. Alternativa mais barata se a
  seção for contestada: adicionar Knowledge à "General" e deixar
  skills/mcp como tabs do Settings modal. **Decisão recomendada:**
  seção Workspace com Knowledge; Skills+MCP ficam como nav dentro do
  Settings (já linkados) + item "Skills & MCP" em General apontando
  para `/settings/skills`.
- **Fontes**: unificar família para `'Inter'` apenas (unicode-range
  split latino/latin-ext fica — é correto para subsetting); remover a
  declaração `'Inter Variable'` duplicada OU apontar `--font-sans` para
  `'Inter'`. Adicionar preload de ambos os woff2 no `index.html`
  (`as="font" type="font/woff2" crossorigin`).
- **Ícones**: adicionar os 13 nomes acima em `Icons.cs` — traço 1.5
  (aproximar do peso Heroicons do OWUIC) ou manter 2.0 por consistência
  interna? **Decisão**: manter stroke 2 atual (consistência visual do
  set próprio > similaridade com OWUIC).
- Sem mudança de comportamento: pin/archive/folders continuam pós-beta —
  os ícones entram já para desbloquear SPEC-135/137 e follow-ups.

Fora de escopo: user menu (SPEC-132), pin/archive/folders de conversas
(pós-beta), redesign do composer, rich model selector, message actions
(regenerate/rate/TTS), banner bar, boot splash custom.

## 3. Requisitos

- RF-001: sidebar ganha entrada para `/knowledge` (seção Workspace ou
  General conforme decisão acima) + item "Skills & MCP" navegável;
  nenhuma rota existente permanece sem caminho de nav.
- RF-002: `index.html` preloada `inter-var-latin.woff2` e
  `inter-var-latin-ext.woff2`; família unificada — uma única declaração
  `@font-face` por subset (latin/latin-ext) e um único nome de família
  referenciado nos tokens.
- RF-003: 13 ícones novos em `Icons.cs` (nomes listados §2), traço
  consistente com o set atual.
- RF-004: i18n — chaves novas de nav (`nav.knowledge`, `nav.skills_mcp`)
  nos 8 locales.
- RF-005: testes — bUnit (sidebar renderiza os novos itens e navega),
  drift guards (`check-css-classes`, `check-i18n-*`) verdes; nenhum
  teste existente quebrado.
- RF-006: regenerar `wwwroot/css/tailwind.css` se classes novas forem
  usadas (não editar manualmente — pipeline tailwind).

## 4. API Contract

Sem mudança de API.

## 5. Critérios de aceite

- [ ] Todas as rotas do `App.razor` alcançáveis por ≤2 cliques a partir
      da shell.
- [ ] Lighthouse/DevTools: fontes preloadadas, sem request waterfall de
      fonte; sem FOUT perceptível no cold-load.
- [ ] `Icon.razor` resolve os 13 nomes novos.
- [ ] `dotnet format` clean, build 0 warnings, bUnit verdes, drift
      guards passando.

## 6. Task Plan

1. Sidebar: seção + itens Knowledge / Skills & MCP + i18n.
2. `index.html` preload + unificação do nome de família `Inter`.
3. `Icons.cs` + 13 ícones.
4. Tailwind regen se necessário + drift guards + bUnit.

## 7. Deliverables

- `Layout/MainLayout.razor` (nav), `wwwroot/index.html` (preload),
  `Styles/app.css`/`app.tailwind.css` (font-family dedup),
  `Components/Icons.cs`, `wwwroot/i18n/*.json`, testes bUnit.

## 8. Referências

- Auditoria visual 2026-11-08 (subagent report): páginas órfãs, fontes
  sem preload, família duplicada, ícones faltantes.
- `~/repos/OpenWebUiCode/src/OpenWebUI.Client/wwwroot/index.html:13` —
  preload pattern.
- `Components/Icons.cs` — dicionário atual (47 nomes, stroke 2).
- `tools/check-*.py` — drift guards que precisam continuar verdes.

## 9. Guardrails

- Não editar `wwwroot/css/tailwind.css` manualmente.
- Não remover rotas — apenas adicionar nav.
- `preload` com `crossorigin` obrigatório (senão double-download).
- Manter ordem unicode-range correta na unificação da família.

## 10. DoD

- Navegação completa (zero rotas órfãs), fontes preloadadas com família
  única, 13 ícones disponíveis, drift guards verdes, i18n paridade nos
  8 locales.
