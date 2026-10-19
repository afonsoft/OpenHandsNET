# SPEC — 44 · Attention Inbox (Conversas Aguardando Você)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `attention-inbox` |
| Type | `Feature / API + Client` |
| Stack | `.NET 10 · EF Core (2 ctx) · SignalR · Blazor WASM` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed (PR #110)` |
| Ticket | [#102](https://github.com/afonsoft/OpenHandsNET/issues/102) |
| Depends on | `04` (API/hub), `09` (runner pending_action), `10` (persistence), `29` (permission ask) |
| Reference | `afonsoft/open-webui` (MIT — port permitido): `AttentionInboxState`, `AttentionBadge.razor`, `chats/attention` endpoints, filtro "Aguardando você" |
| Touches | `src/OpenHandsNET.Infrastructure` (record+runner), `src/OpenHandsNET.Api/Endpoints`, `src/OpenHandsNET.Client` (state, badge, sidebar) |

## 1. User Story

**As a** usuário com várias conversas ativas
**I want** um badge + inbox das conversas que precisam de mim —
confirmação de tool, pergunta do agente, run falhada
**So that** nada fica travado esperando aprovação sem eu notar — hoje a
única superfície é o `ConfirmationBanner` dentro da conversa aberta;
run falhada de outra conversa passa despercebida.

**Contexto:** `pending_action`/`waiting_for_confirmation` existem mas só
via `ConversationStateUpdateEvent` (live, não persistido —
`ConversationRecord` não tem coluna). O fork resolve com count+list
endpoint + badge + filtro de sidebar (poll 30s). Adaptação: **push via
SignalR** (já temos broadcast de state update — sem poll). Decisões
travadas: attention = `permission ask` + `ask_user` + `failed` não vista;
"failed não vista" limpa ao abrir a conversa + endpoint de dismiss.

## 2. Scope (RF)

### RF-001 — Projeção persistida de attention

- `ConversationRecord` ganha `NeedsAttentionKind` (`null |
  confirmation | question | failed`) + `NeedsAttentionAt` (ticks) —
  **migration nos dois contextos** (SQLite + Postgres).
- `AgentRunner` seta ao entrar `waiting_for_confirmation` (permission
  ask → `confirmation`; tool `ask_user` → `question`) e ao marcar run
  `Error` → `failed`; limpa ao receber confirmação/resposta, ao usuário
  enviar mensagem, ou via RF-002 dismiss.
- Índice em `(OrgId, NeedsAttentionAt)` — count/list baratos.

### RF-002 — Endpoints

- `GET /api/v1/app-conversations/attention` → `{count,
  items:[{conversation_id, title, kind, since, excerpt}]}` — scoping
  por user/org igual às rotas existentes.
- `GET /api/v1/app-conversations?attention=1` — filtro na listagem
  existente (sidebar "Aguardando você").
- `POST /{id}/attention/dismiss` — limpa `failed`/`question` lida;
  `confirmation` só limpa pela resposta real (dismiss de ask → equivale
  a `reject`? Não — dismiss vira reject explícito via `ConfirmAction`,
  fora do escopo: dismiss só para `failed`/`question`).

### RF-003 — Client `AttentionInboxState` + `AttentionBadge`

- Serviço scoped: `Count`, `Items`, `Changed`; carrega no boot;
  **invalida** nos broadcasts SignalR existentes
  (`execution_status`/state update do hub — já recebidos) refetch
  debounced 2s. Sem poll.
- `AttentionBadge` (port): chip âmbar com count no header/topbar +
  marcador por item na sidebar + badge mobile no drawer (SPEC-15).
- Sidebar: filtro "Aguardando você" (`?attention=1`); item mostra dot
  por kind (âmbar=confirmation, azul=question, vermelho=failed).

### RF-004 — Deep-link + toast

- Click no item/badge → abre conversa e faz scroll/âncora ao card
  pendente (`ConfirmationBanner`/`QuestionPromptCard` ou `AgentError`).
- Ao subir attention de conversa ≠ aberta → toast "X precisa de você"
  (ToastService existente; opt-out em Settings → `PreferencesJson`).

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET | `/api/v1/app-conversations/attention` | `{count, items[]}` user/org-scoped |
| GET | `/api/v1/app-conversations?attention=1` | filtro na listagem atual |
| POST | `/api/v1/app-conversations/{id}/attention/dismiss` | limpa `failed`/`question` |

Migration: `conversations` + `needs_attention_kind`(text,null) +
`needs_attention_at_ticks`(bigint,null) + índice — **dois contextos**.

## 4. Data model

`ConversationRecord.NeedsAttentionKind` (max 20) +
`NeedsAttentionAtTicks` (long, padrão ticks mirror — ver `CreatedAtTicks`).

## 5. NFR / Security

- Attention nunca vaza cross-org/user — mesma query scope da listagem.
- Campo é projeção, não fonte: drift vs estado real se reconcilia no
  próximo transition do runner (eventual consistency ok — badge é UX,
  não gate).
- `failed` marca mesmo com conversation archivada? Não — Archived exclui
  da projeção.

## 6. Task Plan

1. Colunas + migrations (2 ctx) + set/clear no `AgentRunner` +
   `Error` path; testes de transição.
2. Endpoints attention + filtro + dismiss; testes de contrato + scope.
3. `AttentionInboxState` + `AttentionBadge` + sidebar filter/dots +
   invalidação via hub; bUnit.
4. Deep-link âncora + toast + Settings opt-out.
5. Sweep + `dotnet test` verde.

## 7. Deliverables

Migration (2 ctx), `AttentionEndpoints`, `AttentionInboxState`,
`AttentionBadge.razor`, sidebar updates, toast wiring, testes.

## 8. Referências

- `afonsoft/open-webui` (MIT): `Services/AttentionInboxState.cs`,
  `Components/AttentionBadge.razor`, `Api/Endpoints/ChatEndpoints.cs`
  (`attention/count` + filtro), `Runs/ChatRunApprovals.cs` — modelo.
- `docs/specs/29` (permission ask / pending_action), `docs/specs/15` (mobile
  drawer), `docs/specs/26` (prefs/ToastService).

## 9. Guardrails

- Push-only via SignalR existente — **sem poller novo** no client.
- `confirmation` nunca dismissável (decisão real exigida — SPEC-29).
- Contagem é best-effort UX, nunca gate de segurança.
- `.github/workflows/` intocado; `main` só via PR.

## 10. DoD

- Permission ask noutra conversa → badge++ na sidebar em ≤2s sem reload.
- Abrir a conversa + responder → count decrementa.
- Run failed → item vermelho; abrir/dismiss → limpa.
- Filtro "Aguardando você" lista só as pendentes; scoping org ok.
- `dotnet test` verde (transições + endpoints + bUnit badge).
