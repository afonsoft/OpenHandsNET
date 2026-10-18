# SPEC — 62 · Managed Conversations (`delegate` tool + coordinator wake)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `managed-conversations` |
| Type | `Feature / Agent + API` |
| Stack | `.NET 10 · start-task pipeline · SignalR` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | `TBD` |
| Depends on | `28` (task tool), `04` (start-task), `16` (sleep/wake), `20` (quotas) |
| Reference | competitive-analysis §6.3 (G10); docs.devin.ai
  `/work-with-devin/advanced-capabilities` — managed Devins |
| Touches | `src/OpenHandsNET.{Application,Infrastructure,Api,Client}`, `tests/` |

## 1. User Story

**As a** agente executando uma tarefa grande (migração multi-módulo,
fan-out de testes)
**I want** delegar partes a conversas-irmãs que rodam em sandboxes
próprios, monitorar progresso e agregar resultados
**So that** ganhamos o "managed Devins" do Devin — paralelismo entre
VMs, não só subagentes no mesmo sandbox (o `task` tool da SPEC-28
compartilha o sandbox e o budget do turno).

## 2. Scope (RF)

### RF-001 — Tool `delegate`

- Novas tools no registry:
  - `delegate` `{prompt, title?, selected_repository?, branch?,
    playbook_ref?, max_budget_tokens?, tags?[]}` → cria app-conversation
    **irmã** via pipeline de start-task interno (mesma org, mesmo
    usuário dono) → `{conversation_id}`.
  - `delegate_status` `{conversation_id}` → `{status, summary,
    pull_request_url?}`.
  - `delegate_message` `{conversation_id, message}` → send-message.
  - `delegate_cancel` `{conversation_id}` → stop.
- `parent_conversation_id` + `delegation_depth` na conversa; cap
  `Delegation:MaxDepth=2`, `MaxSiblings=8` (org-configurável).
- Quotas: filhas consomem quota do usuário/org normalmente (SPEC-20)
  + `concurrent_runs` inclui siblings.

### RF-002 — Coordinator wake

- `delegation_watch` interno: conversa-pai inscreve-se nos eventos de
  ciclo de vida das filhas (`run_completed`, `run_failed`,
  `needs_attention`).
- Filha termina → evento `delegate_event` injetado na conversa-pai
  (resumo + outcome) → **acorda a run** (reusa wake-on-message do
  SPEC-16: se o pai dorme, o evento o acorda; se corre, entra como
  observação de tool `delegate_status` pendente).
- Timeout de watch `Delegation:WaitTimeoutMinutes` default 240 —
  expira → `delegate_event` com `status=timeout`.

### RF-003 — Agregação de resultados

- Filha registra `work_summary`/`agent_diagnostic` (SPEC-24) — o pai
  recebe o resumo estruturado (não o transcript inteiro —
  `Delegation:SummaryMaxTokens=2000`, condensado via role `analysis`
  se exceder).
- Pai pode pedir artifacts da filha: `delegate_artifact` `{id, ref}` →
  lê via `IArtifactStore` (SPEC-34) com ACL de mesma-org.

### RF-004 — UI/CLI/audit

- Conversa-pai mostra card `Delegated sessions` (lista, status, link);
  filha mostra badge `child of {id}`.
- Auto-approve: `Delegation:AutoApprove=false` (default) → cada
  `delegate` pede confirmação `ask_user` antes de criar a filha
  (igual "Auto-approve child sessions" do Devin).
- Audit `delegate.spawned`, `delegate.completed`, `delegate.cancelled`.

### RF-005 — CLI

- `openhandsnet delegate <prompt> [--repo] [--watch]` — cria filha
  direto (sem pai) e opcionalmente espera; `children <id>` lista.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET | `/api/v1/app-conversations/{id}/children` | filhas |
| POST | `/api/v1/app-conversations/{id}/delegate` | `{prompt,…}` → `{conversation_id}` (equivale à tool, p/ UI) |

(ferramentas do agente não são REST — schema de tool documentado no
OpenAPI como `x-tools` extension ou em `GET /{id}/tools` existente.)

Erros: `403` quota/depth/siblings cap · `404` filha · `409` cancel.

## 4. Data model

`conversations` + `ParentConversationId`, `DelegationDepth`,
`DelegationMetaJson` (prompt original, tags); índice por parent.
Migration.

## 5. NFR / Security

- Filha herda: mesma org, owner, políticas de segurança, secrets do
  usuário (via `SecretNamesJson` — valores nunca no prompt).
- Depth cap e sibling cap são hard limits (403 — não silencioso).
- `delegate` nunca escala permissões — filha tem mesmos direitos do
  pai (incl. `AutoApprove` herdado).
- Custo: `max_budget_tokens` por filha respeitado pelo runner.

## 6. Task Plan

1. Campos de parentesco + `delegate` tool (cria start-task interno).
2. Watch/wake (`delegate_event` + wake-on-event).
3. `delegate_status/message/cancel/artifact` + agregação de resumo.
4. Auto-approve gate + audit + quotas.
5. UI cards + CLI + docs.

## 7. Deliverables

Agente cria sessões irmãs em sandboxes próprios, é acordado no
término, agrega resumos e artifacts; visibilidade na UI; caps de
segurança.

## 8. Referências

- `migration-plan/research/competitive-analysis-devin-openhands-cloud.md`.
- `.specs/28_subagents_task_tool_spec.md` (distinção: in-sandbox vs
  cross-sandbox), `.specs/16_persistent_sandbox_lifecycle_spec.md`
  (wake), `.specs/34_streaming_deltas_artifacts_spec.md` (artifacts).

## 9. Guardrails

- `delegate` ≠ `task`: task segue in-sandbox/rápido; delegate é
  cross-sandbox/auditado — documentar quando usar cada um.
- Sem recursão além de depth 2 (avalia clusters de conversas).
- Nunca repassar tokens/scopes além dos do dono.

## 10. DoD

- `delegate` cria conversa filha real (teste integração) com
  `parent_conversation_id`.
- Filha completando acorda o pai com `delegate_event` + resumo.
- Depth/sibling cap → 403; auto-approve=false pede `ask_user`.
- `delegate_message` entrega; `delegate_cancel` para a filha.
- Suite verde; golden OpenAPI atualizado.
