# SPEC — 135 · Conversation UX: fork + turn undo + diff hunk revert

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `beta-conversation-ux` |
| Type | `Feature` |
| Stack | `.NET 10 · Blazor WASM` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | #369 · Epic #365 |
| Scope | `src/` + `Client/` + `tests/` + golden OpenAPI |

## 1. User Story

**As a** usuário, **I want** bifurcar uma conversa a partir de qualquer
ponto, desfazer a última rodada do agente (arquivos + mensagens), e
reverter um hunk individual do diff, **so that** eu explore caminhos
alternativos e recupere de erros sem recomeçar — o trio que OpenCode
(`/undo`, fork) e Devin (remix, checkpoints) já entregam.

## 2. Contexto / escopo

Três gaps coesos de manipulação da conversa; reaproveitam infra que já
existe:

- **Fork**: `EventBase.parent_id` existe no modelo de eventos V1 mas
  nada o consome para bifurcação. Devin ("remix") e OpenCode
  (sessões-filha) têm. Implementação: `POST /app-conversations/{id}/fork`
  clona eventos até `at_event_id` (ou todos) numa nova conversa,
  encadeando `parent_id` no primeiro evento clonado, e **reusa o
  checkpoint do workdir** daquele turno como snapshot inicial da nova
  conversa — a sandbox nova materializa o mesmo estado de arquivos.
- **Turn undo**: checkpoints (`CheckpointService`, `/checkpoints` +
  revert com drift-guard, SPEC-43) protegem arquivos, mas não rewound
  o stream de eventos/mensagens. OpenCode `/undo` desfaz o turno
  inteiro. Implementação: `POST /app-conversations/{id}/undo` →
  (1) identifica o último turno do usuário (último `MessageEvent`
  `source=user`), (2) trunca eventos persistidos posteriores,
  (3) reverte arquivos para o checkpoint do início do turno
  (pre-existente por turno — SPEC-43 auto-checkpoint), (4) emite
  `conversation_state` update + `files_changed`. Guarda: 409 com run
  ativa (mesmo guard do revert).
- **Diff hunk revert**: `DiffTab.razor` mostra diff read-only.
  Implementação: `POST /sandboxes/{id}/files/revert-hunks
  {path, hunk_indexes[]}` — sandbox-agent gera reverse-patch do hunk
  (`git diff` + apply -R) — e o `DiffTab` ganha botão "reverter" por
  hunk. Também "comment → send to agent": por ora só o revert (o
  comment-to-agent é pós-beta; deixar slot no menu do hunk).

Fora de escopo: undo multi-nível (>1 turno por chamada — compor chamadas
resolve), fork compartilhado entre usuários, comment-to-agent no diff,
revert de commits git do usuário (escopo = mudanças não-commitadas do
agente + events do ledger).

## 3. Requisitos

- RF-001: `POST /api/v1/app-conversations/{id}/fork {at_event_id?,
  title?}` → nova conversa `{id, parent_conversation_id}`; eventos
  clonados até o ponto (ordem/ULIDs novos, `parent_id` encadeado);
  checkpoint do turno do corte vira snapshot inicial; sem run ativa na
  origem (409); ownership herda do chamador.
- RF-002: UI — ação "Fork" no menu da conversa (sidebar item menu +
  topbar overflow); conversas-filhas mostram link "originada de …"
  (badge parent no topo da `ConversationPage`).
- RF-003: `POST /api/v1/app-conversations/{id}/undo` — trunca eventos
  do último turno + revert de arquivos ao checkpoint do início do turno;
  409 se run ativa ou sem turno anterior; emite `files_changed` +
  `conversation_state` via hub; idempotente (segundo undo sem novo
  turno → 404/`no_turn`).
- RF-004: slash `/undo` no `ChatComposer` (mesmo endpoint) + botão
  "Desfazer turno" na topbar com confirmação `DialogService`.
- RF-005: `POST /api/v1/sandboxes/{id}/files/revert-hunks
  {path, hunk_indexes}` → sandbox-agent executa `git diff` +
  reverse-apply por hunk; erro limpo quando hunk não aplica
  (`409 {conflicts}`); respeita o mesmo auth/ownership das file-ops.
- RF-006: `DiffTab` — botão revert por hunk (header do hunk), estado de
  loading + resultado; menu do hunk com slot "comentar → agente"
  desabilitado/hint "em breve".
- RF-007: testes — unit (seleção do ponto de fork/undo, reverse-patch
  de hunk), integration (fork cria conv com eventos+checkpoint; undo
  trunca+reverte+idempotente; revert-hunks aplica/falha 409), bUnit
  (botões/confirm dialogs).
- RF-008: i18n ×8 para `fork`, `undo`, `revert_hunk`; golden OpenAPI
  regen (3 rotas novas).

## 4. API Contract

| Método | Rota | Auth | Notas |
|---|---|---|---|
| POST | `/api/v1/app-conversations/{id}/fork` | sessão | `{at_event_id?, title?}` → `{id, parent_conversation_id}` |
| POST | `/api/v1/app-conversations/{id}/undo` | sessão | 409 run ativa / 404 `no_turn` |
| POST | `/api/v1/sandboxes/{id}/files/revert-hunks` | sessão | `{path, hunk_indexes[]}` → `{applied[]}` / 409 `{conflicts}` |

## 5. Critérios de aceite

- [ ] Fork cria conversa navegável com histórico até o corte e arquivos
      do checkpoint correspondente; badge de origem visível.
- [ ] Undo apaga eventos do último turno E restaura arquivos; run ativa
      → 409; segundo undo seguido → `no_turn`.
- [ ] Revert-hunk aplica só o hunk pedido e falha limpo em conflito.
- [ ] `dotnet format` clean, build 0 warnings, testes verdes, golden
      regerado, drift guards ok.

## 6. Task Plan

1. Serviço de fork (clone eventos + link parent + checkpoint link) +
   endpoint + menu/topbar UI.
2. Serviço de undo (seleção do turno + truncate + checkpoint revert) +
   endpoint + slash/topbar.
3. Sandbox-agent `revert-hunks` + DiffTab botões.
4. i18n, golden regen, testes de ponta a ponta.

## 7. Deliverables

- Handlers em `AppConversationEndpoints.cs`/`SandboxEndpoints.cs`,
  serviços `ConversationForkService`/`TurnUndoService`, agent handler
  `revert-hunks`, UI (menu item, badge, confirm dialog, DiffTab botão),
  i18n, testes, golden regen.

## 8. Referências

- `src/OpenHandsNET.Api/Endpoints/CheckpointEndpoints.cs` — guards de
  revert (run ativa, 409 conflicts, snapshot pre-revert).
- `src/OpenHandsNET.Domain/…/EventBase.cs` — `parent_id` existente.
- `Components/Conversation/DiffTab.razor` + `ChatComposer.razor`
  (`ComposerSlash.cs` — slash commands).
- `SandboxAgent/Program.cs` — file-ops existentes (mesmo padrão do novo
  handler).
- SPEC-43 (checkpoints), SPEC-56 (share/download — padrão de novas rotas
  de conversa), SPEC-62 (delegate — parent/child já emitido em eventos).

## 9. Guardrails

- Truncate de eventos **só** ocorre via endpoint com guards — nunca no
  client; ledger permanece append-only fora do escopo do undo
  (eventos removidos do stream ativo ficam arquivados na linha do
  checkpoint — decisão: soft-delete por `truncated_at` marcador em vez
  de hard delete, auditável).
- Fork nunca copia secrets/conteúdo sensível além dos eventos + workdir.
- Revert-hunks não toca em arquivos untracked nem fora do workdir.
- Undo durante run ativa é proibido (drift) — mesmo guard SPEC-43.

## 10. DoD

- Três fluxos operacionais end-to-end na UI + API; regressões de
  checkpoint/hub verdes; golden + i18n + drift guards ok; acceptance
  manual em conversa real (fork → divergência, undo → estado anterior,
  hunk revert → diff atualizado).
