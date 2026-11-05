# SPEC — 119 · Dialog infra + Settings modal (OWUIC parity)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `dialogs-settings-modal` |
| Type | `Frontend` |
| Status | `Approved` |
| Ticket | #321 (Epic #317) — gap-analysis OpenWebUiCode 2026-11-04 |
| Scope | `src/OpenHandsNET.Client/` (`Services/DialogService`, `Components/DialogHost`, `ShareDialog`, `MediaLightbox`, `SettingsModal`, `Settings/*`, páginas Settings/Admin) |

## 1. User Story

**As a** usuário do OpenHandsNET, **I want** confirmações, diálogos e
o painel de settings no padrão modal do OpenWebUiCode (rail de abas
lateral + seções nomeadas + barra de salvar fixa), **so that** fluxos
destrutivos sejam consistentes e settings escalem sem virar página
infinita.

## 2. Contexto / escopo

AS-IS: sem infra de dialogs — confirmações hoje provavelmente usam
`window.confirm` via JS ou banners inline (`ConfirmationBanner` é
para permissões de agente, não UI genérica); `SettingsPage` é página
cheia; sem lightbox de mídia; sem share dialog por conversa.

OWUIC provê o kit completo:

- `DialogService` + `DialogHost.razor`: fila tipada de diálogos
  (confirm/alert/prompt/custom), `Task<bool>` awaitable, ESC/backdrop
  configuráveis, focus-trap.
- `SettingsModal.razor`: `.modal` + `.modal-tabs` (rail horizontal
  rolável no mobile → vertical `sm:w-60`), `.modal-tab[.active]`,
  `.modal-body`; `Settings/SettingsSection.razor`
  (`.settings-section*`) + `SettingsSaveBar.razor` (`.save-bar`
  sticky + `.save-bar-msg`).
- `ShareDialog.razor`: link público + expiração/revoke.
- `MediaLightbox.razor`: overlay p/ imagens/anexos.
- `OfflineRetry.razor`: retry com backoff visual.

Direção: portar o kit para `Components/`+`Services/` do OHN usando as
classes da SPEC-116 (`.modal*`, `.settings-*`, `.save-bar*`, `.menu`).
Settings vira modal acessado da sidebar **mantendo** a rota
`/settings` (deep-link abre o modal sobre a home) — decisão
recomendada; alternativa é manter página e usar só os componentes.

Fora de escopo: conteúdo das abas de settings (já existe em
`SettingsPage`/`McpSettingsPage`/`SkillsSettingsPage` — só
reorganizar em seções); admin pages.

## 3. Requisitos

- RF-001 — **DialogService**: `Confirm(title, body, confirmLabel)`
  → `Task<bool>`; `Alert`, `Prompt`, `Show<TComponent>(params)`;
  empilhamento (múltiplos dialogs), ESC/backdrop por instância,
  focus-trap + retorno de foco, `aria-modal`/`role="dialog"`.
- RF-002 — **DialogHost**: host único no `MainLayout`; renderiza fila
  com `.modal-backdrop`/`.modal`; animação de entrada respeitando
  reduced-motion.
- RF-003 — **Migração de confirmações**: substituir confirms inline/
  JSInterop existentes (delete conversation, revoke key, ações admin
  destrutivas — inventariar via grep `IJSRuntime`/confirm).
- RF-004 — **SettingsModal**: rail de abas com as seções atuais
  (General/Appearance, MCP, Skills, + futuras), `SettingsSection`
  (title+desc) e `SettingsSaveBar` (sticky, dirty-state, save/discard
  + msg); deep-link `/settings` abre o modal (rota preservada).
- RF-005 — **ShareDialog**: gera/copíe link de
  `/shared/conversations/{id}` (rota `SharedConversationPage` já
  existe — verificar endpoint de share/token; se ausente, diálogo só
  copia URL pública existente).
- RF-006 — **MediaLightbox**: overlay de anexo/imagem (zoom, ESC,
  backdrop click, `aria-label`); integrar a `AttachmentThumb`/
  FileView onde aplicável.
- RF-007 — **OfflineRetry**: banner/ação com retry exponencial onde
  `OpenHandsNetApiClient` falha por rede (integra com `OfflineBanner`).
- RF-008 — **i18n/a11y**: strings ×8; touch target ≥ 24 px; foco
  visível em tabs (anel da SPEC-116); `.modal` vira `max-sm:h-dvh`
  full-screen no mobile como OWUIC.

## 4. Acceptance

- [ ] `DialogService.Confirm` awaitable usado em ≥1 fluxo real
      (ex.: delete conversation) com bUnit cobrindo confirm/cancel.
- [ ] Settings abre como modal com rail de abas; `/settings` ainda
      resolve; SaveBar aparece só com dirty-state.
- [ ] ShareDialog copia link funcional da conversa compartilhada.
- [ ] Lightbox abre anexo em overlay acessível (ESC/backdrop/fechar).
- [ ] Nenhum `JSInterop confirm(` restante (grep = 0).
- [ ] drift guards + i18n parity verdes; `dotnet test` verde.

## 5. API Contract

Sem contratos novos nesta SPEC. Se o endpoint de share precisar de
revoke/expiry, documentar divergência aqui e abrir follow-up API —
o diálogo funciona com a URL atual.

## 6. Task Plan

1. `DialogService`+`DialogHost` + focus-trap + bUnit.
2. Migrar confirmações existentes (inventário por grep).
3. `SettingsSection`+`SettingsSaveBar`+`SettingsModal` + rota.
4. `ShareDialog` + `MediaLightbox`.
5. `OfflineRetry` + i18n + baselines + docs.

## 7. Riscos / notas

- Modal × página de settings: a decisão recomendada (modal + rota
  preservada) é reversível — o rail de abas também funciona em página.
- Focus-trap em Blazor WASM: implementar via JS interop mínimo
  (loop Tab no container); não reinventar — OWUIC `DialogHost` é a
  referência.
- Empilhamento z-order com toasts/`ToastHost` existente — escala
  definida (modal acima de banner, toast acima de modal).
