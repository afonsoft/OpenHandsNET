# SPEC — 121 · Agent tools expansion (parity com os 64 builtins OWUIC)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `agent-tools-expansion` |
| Type | `Feature` |
| Status | `Approved` |
| Ticket | #323 (Epic #317) — gap-analysis OpenWebUiCode 2026-11-04 |
| Scope | `src/OpenHandsNET.Infrastructure/Agent/Tools/`, `Application/Agent/`, `Domain/Events/`, `SystemPrompt/` |

## 1. User Story

**As a** operador do OpenHandsNET, **I want** o agente com o mesmo
repertório de tools do OpenWebUiCode (file ops completas, patch,
jobs assíncronos, contexto/memória, notebooks, planejamento,
worktrees), **so that** runs autônomas longas não travem em bash
síncrono nem precisem de workarounds para operar o workspace.

## 2. Contexto / escopo

AS-IS (`BuiltinTools.cs`, 21 tools): `bash`, `file_read/write/edit`,
`think`, `task_tracker`, `git_commit`, `ask_user`,
`create_pull_request`, `checkpoint`, `routine`, `memory`, `browser`,
`invoke_skill`, `search`, `web_fetch`, `task`, `agent_catalog`,
`task_status`, `task_result`, `finish`.

OWUIC (64 tools, `ChatTools/Tools/`): o delta confirmado é —

| Família | Tools OWUIC | Estado OHN |
|---|---|---|
| File ops | `file_list`, `file_grep`, `file_glob` | ausentes |
| Patch | `apply_patch` (+`UnifiedDiff`) | ausente |
| Code intel | `code_search`, `lsp_*`×6 | ausente → SPEC-122 |
| Jobs async | `job_list/output/kill` | ausente (bash síncrono ≤900 s) |
| Contexto | `context_notes/save/recovery/compact`, `extend_context`, `instant_context`, `new_context` | ausentes (compaction é interna) |
| Memória | `memory_save/search/learn` | `memory` monolítico |
| Notebooks | `notebook_read/edit` | ausente |
| Planejamento | `write_plan`, `plan_exit`, `specs` | ausentes |
| Colaboração | `unseen_messages`, `reminder`, `run_result`, `rewind` | ausentes |
| Git avançado | `worktree_diff/merge` | ausente |
| Teams | `team_message/status/task` | ausente (subagents 1:1 só) |
| Util | `todo_write`, `tool_search` | `task_tracker`≈todo; tool_search → SPEC-123 |
| Product-only | `generate_image/video`, `n8n_*`, `code_interpreter` | fora de escopo (não se aplica ao produto) |

Mapeamentos já equivalentes: `shell_exec`≈`bash`,
`fetch_url`≈`web_fetch`, `web_search`≈`search`, `skill`≈`invoke_skill`,
`delegate_task`≈`task`, `browser_*`×5≈`browser` (avaliar split só se
o protocolo único limitar — registrar decisão).

Fora de escopo: LSP (SPEC-122), tool_search/deferred loading + hooks
+ permission ruleset (SPEC-123), generate_image/video, n8n,
code_interpreter.

## 3. Requisitos

Prioridade P1 (esta SPEC):

- RF-001 — **File ops**: `file_list` (glob/depth/type filter),
  `file_grep` (regex, context lines, max results — wrapper rg quando
  disponível, fallback `grep -R`), `file_glob` (pattern + sorted
  mtime) — todos via `Executor` no sandbox, output truncado com
  `total`/`,truncated` como `file_read`.
- RF-002 — **apply_patch**: unified-diff multiline apply
  (`UnifiedDiff` port — add/update/delete/move hunks com validação
  de contexto), emite `files_changed`/`patch_applied` events; fallback
  documentado → `file_edit`.
- RF-003 — **Jobs**: `job_list/output/kill` — processos background do
  sandbox com id, comando, estado, tail; bash ganha arg
  `run_in_background` que retorna `job_id` (mantém sync default).
- RF-004 — **Memória estruturada**: split `memory` →
  `memory_save`/`memory_search`/`memory_learn` (tags, scope
  user/repo/session; busca por substring+recency); manter alias
  `memory` 1 release com deprecation note no schema.
- RF-005 — **Contexto agent-facing**: `context_notes` (notas persistentes
  do run), `context_save`/`context_recovery` (snapshot nomeado →
  restaura em new_context/crash), `context_compact` (força condensação
  — chama `LlmSummarizingCondenser` existente), `extend_context`/
  `instant_context` (eleva budget do próximo turno — integra
  `HeuristicContextMeter`), `new_context` (novo contexto mantendo
  snapshots/notas).
- RF-006 — **Planejamento**: `write_plan` + `plan_exit` (plan-mode:
  escreve plano em `.openhandsnet/plans/<id>.md`, sai do modo) +
  `specs` (lê/lista `.specs/` do workspace) — par com `PlanFileService`
  do OWUIC (`.openwebui/plans`).
- RF-007 — **Notebooks**: `notebook_read`/`notebook_edit` (cell ops:
  replace/insert/delete, .ipynb no workspace — paridade do nosso
  harness, que já tem esses tools p/ agentes).
- RF-008 — **Colaboração/run**: `unseen_messages` (fila de msgs do
  usuário chegadas durante a run — consome o canal de mensagens
  existente), `run_result` (resultado estruturado de run anterior),
  `rewind` (volta N passos no event stream — par com `checkpoint`),
  `reminder` (agenda lembrete na conversa — par com `routine` existente;
  avaliar fundir).
- RF-009 — **Worktrees**: `worktree_diff`/`worktree_merge` (diff de
  worktree p/ main + merge assistido — dentro do sandbox).
- RF-010 — **todo_write**: alias/renomeação de `task_tracker` para o
  nome canônico `todo_write` (compat: manter `task_tracker` como
  alias 1 release) — paridade de nomenclatura com OWUIC/harness.

P2 (registrar como follow-up, não implementar): `team_*` (multi-agent
teams), `code_interpreter`, split `browser_*` se necessário.

- RF-011 — **Prompt/schema sync**: `PromptContributors`/`AgentTools`
  atualizados (novos tools aparecem com descrições no estilo atual);
  `SubagentPresets` allowlists revisadas p/ novos tools seguros.
- RF-012 — **Eventos**: novos kinds de evento somente se necessário
  (`patch_applied`, `job_state`) — modelo V1 (`kind`, ULID,
  `parent_id`), documentado na seção 5.

## 4. Acceptance

- [ ] Cada tool novo tem handler + schema JSON + teste de unidade
      (happy + erro); `ToolRegistry` os lista.
- [ ] `job_list` reflete um `bash(run_in_background)` real; `job_kill`
      termina o processo no sandbox.
- [ ] `apply_patch` aplica diff válido e falha limpo em contexto
      divergente (teste).
- [ ] `memory_*` persiste entre turns; alias `memory`/`task_tracker`
      continuam funcionando (deprecation nos schemas).
- [ ] `write_plan` cria `.openhandsnet/plans/*.md` visível via
      `file_read`; `plan_exit` restaura modo execução.
- [ ] Subagent presets não ganham tools destrutivos por acidente
      (teste de allowlist).
- [ ] `dotnet test` verde; sem regressão nos 21 tools atuais.

## 5. API Contract / Event model

Tools novos seguem o contrato `AgentTool`/`ToolSchema` atual
(`name`, `description`, JSON schema, `Risky`). Eventos novos
(`kind` snake_case, `id` ULID, `source`, `parent_id`):
`job_state` (job_id/status/exit_code), `patch_applied` (paths),
`plan_written` (path). Nenhum endpoint REST novo; schemas são
contrato público do agente → documentar em
`migration-plan/research/api-inventory.md` divergências vs upstream.

## 6. Task Plan

1. RF-001 file ops + testes.
2. RF-002 apply_patch + `UnifiedDiff` + eventos.
3. RF-003 jobs + `run_in_background`.
4. RF-004 memory split + aliases.
5. RF-005 context suite.
6. RF-006/7 plan+specs+notebooks.
7. RF-008/9 collab+worktree.
8. RF-010/11/12 aliases, prompts, presets, eventos, docs.

## 7. Riscos / notas

- `BuiltinTools.cs` já tem 1586 linhas — novos tools em arquivos
  por família (`Tools/FileTools.cs`, `Tools/JobTools.cs`, …) como no
  OWUIC (`ChatTools/Tools/*.cs`), não inflar o arquivo.
- Jobs background: ciclo de vida preso ao sandbox (SPEC-16 persistent
  lifecycle) — documentar comportamento quando sandbox pausa.
- Aliases deprecated: schemas devem marcar `"deprecated": true` +
  descrição "use X" — LLM precisa preferir o nome novo.
- `unseen_messages` depende do canal de mensagens mid-run — verificar
  suporte em `ConversationHubClient`/fila antes da tarefa 7.
