# SPEC — 118 · Shell sidebar + notification UX (Devin/OWUIC parity)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `shell-sidebar-ux` |
| Type | `Frontend` |
| Status | `In review` |
| Ticket | #320 (Epic #317) — gap-analysis OpenWebUiCode 2026-11-04 |
| Scope | `src/OpenHandsNET.Client/Layout/MainLayout.razor`, `Components/` (Sidebar extraído, NotificationBell, BannerBar), `Services/` |

## 1. User Story

**As a** usuário do OpenHandsNET, **I want** uma sidebar de sessões
com busca, ações por conversa e centro de notificações, **so that**
a navegação entre sessões fique no padrão Devin Cloud / OpenWebUiCode
(rail colapsável + lista gerenciável) em vez de uma lista de links
somente-leitura.

## 2. Contexto / escopo

AS-IS (`MainLayout.razor`): sidebar colapsável (~260 px → rail),
filtro por texto, dots de status/attention, AttentionBadge/Feed,
mobile drawer, skip-link — funcional mas:

- itens de conversa são `<a>` puros — **sem menu contextual**
  (pin/rename/archive/delete existem na API/upstream? verificar
  endpoints antes do plano — se ausentes, o item vira `[A DEFINIR]`
  ou implementa só client-side pin/order);
- rail colapsado é minimal: sem botão new-chat com logo, sem sino;
- sem `NotificationBell` (centro de notificações da app — distinto do
  `AttentionFeed`, que é inbox de eventos de agente);
- sem `BannerBar` (faixa global: avisos admin, manutenção, versão);
- lista fixa `Take(20)` sem "load more"/virtualização;
- sem padrão de empty-state (`sidebar-empty`/`chat-empty` do OWUIC).

OWUIC `Sidebar.razor` (916 linhas) mostra o padrão-alvo: rail de
42 px com favicon→new-chat + expand + bell + attention; lista com
search box interno; seções (channels/chats); menu `⋯` por item
(pin/rename/archive/delete); footer com user menu.

Direção: extrair a sidebar do `MainLayout` para
`Components/SessionSidebar.razor` (facilita teste bUnit) e portar os
padrões OWUIC adaptados a "conversations" (não chats/channels).

Fora de escopo: backend de pin/rename/archive se o endpoint não
existir (marcar follow-up); channels/DMs/folders (produto OWUIC —
não se aplica); settings modal (SPEC-119).

## 3. Requisitos

- RF-001 — **Extração**: `SessionSidebar.razor` dedicado, mesma
  a11y atual (dialog/aria-modal mobile, Escape, aria-expanded);
  `MainLayout` só orquestra.
- RF-002 — **Rail enriquecido**: modo colapsado com logo→new-session,
  expand (topo+rodapé), `NotificationBell`, `AttentionBadge` — 42 px,
  ícones 30 px hit-area, paridade OWUIC.
- RF-003 — **Busca na sidebar**: input com lupa dentro da lista
  (estilo OWUIC: fundo transparente, placeholder i18n) substituindo
  o `sb-filter` atual; debounce 150 ms.
- RF-004 — **Menu por conversa**: botão `⋯` por item (visible em
  hover **e** `group-focus-within` — correção do finding A2 do
  design-review OWUIC); ações: pin/unpin (client-side persistido),
  rename (se endpoint existir — senão só título local), archive,
  delete (com confirmação via DialogHost da SPEC-119). Verificar
  `OpenHandsNetApiClient`/`AdminApiClient` p/ endpoints reais antes
  de implementar; os que não existirem entram como "pendente API" na
  SPEC com menu ocultando-os.
- RF-005 — **NotificationBell**: componente com dot de não-lidas,
  dropdown `.menu` listando notificações (fonte: AttentionInboxState
  + eventos de sistema futuros); a11y `aria-label`/badge count.
- RF-006 — **BannerBar**: faixa top-level dismissável (ícone + texto +
  i18n) alimentada por estado app (`OfflineBanner` vira caso dela —
  manter OfflineBanner se já cobre; BannerBar p/ avisos genéricos:
  versão nova, manutenção, flags admin).
- RF-007 — **Lista**: "load more" ou scroll infinito além das 20
  primeiras; `sidebar-empty` quando filtro/attention zera; dot de
  attention por kind (já existe) + tooltip i18n com o motivo.
- RF-008 — **i18n/a11y**: todas as strings novas nos 8 locales
  (ratchet); menu `⋯` via `role="menu"`/`menuitem` + navegação por
  setas; touch targets ≥ 24 px (OWUIC A4 corrigido lá — não repetir).

## 4. Acceptance

- [ ] Rail colapsado replica o padrão OWUIC (logo/new/expand/bell/
      attention) e persiste `ohn.sidebar`.
- [ ] Menu `⋯` acessível por teclado (`group-focus-within`) e toque;
      ações disponíveis executam ou ficam ocultas com nota.
- [ ] Busca filtra título/repo; empty-state aparece sem resultados.
- [ ] NotificationBell mostra dot + dropdown; AttentionFeed continua
      funcionando (não regredir SPEC-44).
- [ ] BannerBar renderiza um banner dismissável de exemplo (flag).
- [ ] bUnit nos componentes novos (sidebar item menu, bell,
      empty-state); drift guards + i18n parity verdes.
- [ ] Sem mudança de contrato salvo endpoints novos documentados
      na seção 5 (se pin/archive/delete precisarem de API).

## 5. API Contract

Reuso dos endpoints existentes (`GET /api/v1/app-conversations`,
`DELETE` se existir). **Verificar na implementação**: presença de
`PATCH`/`DELETE` por conversa; se ausente → menu exibe apenas pin
local + navegação, e follow-up API é aberto (referência:
`migration-plan/research/api-inventory.md`).

## 6. Task Plan

1. Extrair `SessionSidebar` + bUnit base.
2. Rail OWUIC + busca interna + empty-states.
3. Menu `⋯` por item (verificar endpoints primeiro).
4. `NotificationBell` + `BannerBar` (+ reaproveitar OfflineBanner).
5. Load-more/virtualização leve.
6. i18n ×8 + baselines + docs.

## 7. Riscos / notas

- `AttentionFeed` vs `NotificationBell`: não duplicar conceito —
  bell agrega notificações de app/sistema; feed continua o inbox de
  attention de agente. Nomes i18n devem deixar a diferença clara.
- Endpoints inexistentes para rename/archive são o maior risco de
  escopo — resolver na tarefa 3 antes de prometer ações no menu.
- Folders/tags por conversa ficam fora (produto diferente) — anotar
  como decisão, não gap.
