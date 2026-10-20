# SPEC-75: Layout shell — mobile drawer, rail e a11y (paridade open-webui)

| Campo | Valor |
|---|---|
| Status | Draft |
| Gap key | GAP-architecture-layout-shell-mobile |
| Type | UX/Layout |
| Fonte | Análise comparativa `afonsoft/open-webui` (Open-WebUI+Code) × `src/OpenHandsNET.Client` |

## User Story

Como usuário do OpenHandsNET em telas pequenas e usuário de teclado/leitor
de tela, quero a mesma ergonomia de shell do open-webui: sidebar que vira
gaveta no mobile com backdrop, rail colapsada rica e landmarks de
acessibilidade.

## Contexto técnico

- `src/OpenHandsNET.Client/Layout/MainLayout.razor` — sidebar fixa
  `aside.sidebar` sem drawer/backdrop; `@media (max-width: 860px)` em
  `wwwroot/css/app.css` apenas esconde/encolhe.
- Referência: `src/OpenWebUI.Client/Layout/MainLayout.razor` +
  `Components/Sidebar.razor` no repo `afonsoft/open-webui` (mesmo stack
  Blazor WASM + Tailwind).

## Requisitos

1. **Mobile drawer**: abaixo de `md`, sidebar vira gaveta fixa
   (`fixed inset-y-0 left-0`, `translate-x`) com backdrop
   `bg-gray-950/50` que fecha ao clicar; `role="dialog"` +
   `aria-modal="true"`; Escape fecha.
2. **Topbar mobile** (`md:hidden`, h-11): hamburger + título +
   `AttentionBadge` — estado `_mobileMenu` controlado pelo layout.
3. **Rail colapsada (42px)**: botão expandir no topo **e** no rodapé
   (botão único fica fora da viewport em janelas altas — bug upstream
   corrigido), `AttentionBadge` dentro do rail.
4. **A11y**: `<a class="skip-link" href="#main-content">` +
   `main#main-content` `tabindex="-1"`; `aria-live` no indicador de
   conexão; `aria-expanded` nos botões hamburger/collapse.
5. **Notification feed**: dropdown sobre `AttentionInboxState` (badge
   unread + lista clicável que navega p/ conversa e marca lida) —
   reusa o feed de attention existente, sem backend novo.
6. Persistência do estado colapsado em `localStorage` (`ohn.sidebar`)
   preservada; desktop sem regressão visual.

## Critérios de aceite

- Em viewport < 768px: hamburger abre gaveta, backdrop fecha, Escape fecha.
- Em desktop: rail 42px com expand top+rodapé funcional.
- `dotnet build` 0w/0e; teste bUnit cobrindo toggle da gaveta e skip-link.
- Nenhuma regressão nas rotas existentes.

## Fora de escopo

Folders/pinned chats na sidebar (SPEC futura), CommandPalette
existente, auth flow.
