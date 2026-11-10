# SPEC — 144 · Batch Refactor Orchestration (grafo de deps → lotes → PRs)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `batch-refactor` |
| Type | `Feature / Agent orchestration + SCM` |
| Stack | `.NET 10 · delegate (SPEC-62) · git · PR per batch` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | `GAP-REFACTOR-BATCH` (docs/research/2026-11-competitive-gap-analysis.md §G5) |
| Depends on | `62` (delegate), `52` (PR create/status), `141` (autofix loop p/ CI dos lotes) |
| Reference | OH Refactor SDK / Large Codebase SDK (dependency graph → batches → verify/fix → tracking); Devin stacked PRs — conceitos apenas |
| Touches | `src/Sandcastle.{Application,Infrastructure,Api,Client}`, `tests/` |

## 1. User Story

**As a** mantenedor com refatoração grande (migração de framework,
upgrade de dependência, enforce de convenção)
**I want** que o Sandcastle decomponha o trabalho em lotes revisáveis
ordenados por dependência, execute agentes em paralelo (3–5) e abra um
PR por lote com tracking de progresso
**So that** um projeto de semanas vira dias, com human-in-the-loop por
PR — paridade com o Refactor SDK do OpenHands. Hoje `delegate` cria
filhas soltas; não existe decomposição em grafo nem batch tracking.

## 2. Scope (RF)

### RF-001 — Plan (`POST /api/v1/refactors`)

- Entrada: `{repositories[], goal, strategy?: directory|import-graph,
  max_concurrency?: 5, batch_size_hint?, stacked?: bool}`.
- Sessão **planner** (`agent_type: refactor-planner`) clona o repo e
  produz `refactor_plan_v1`: grafo de dependências (import analysis por
  linguagem — C# `using`/proj refs, TS/JS imports, Python imports;
  fallback: fronteira de diretório), lotes ordenados topologicamente
  com `depends_on[]`, por lote: `files[]`, `description`, `verify_cmd`
  (build+test gate), `risk` (low|med|high).
- Usuário revisa/edita o plano (UI: batch graph + lista) antes de
  `Start` — hard gate de aprovação (`Refactor:RequirePlanApproval=true`).

### RF-002 — Execução

- `BatchExecutor` (hosted service ou conversa coordenadora — decisão:
  **conversa coordenadora** `agent_type: refactor-coordinator` p/
  steering via chat): respeita `depends_on` (lote só começa quando
  deps merged) e `max_concurrency` (default 5, cap 8).
- Por lote: conversa filha via `delegate` (repo + branch
  `refactor/{plan_id}/{n}`), prompt com files+goal+verify_cmd; filha
  edita, roda verify_cmd, fixa até passar (bounded: `Refactor:
  MaxVerifyFixLoops=5`) → `ScmPullRequestService.CreateAsync` →
  PR por lote.
- `mode: stacked` (GitHub-only): PRs em cadeia (base de cada um = head
  do anterior); `mode: independent` (default): cada PR baseia em
  `main`, lotes devem ser realmente independentes (planner valida
  sobreposição de arquivos — overlap>0 exige `stacked` ou sequencial).
- Reconciliação: `pull_request` merged webhook (SPEC-140 ingestão)
  desbloqueia dependentes; feedback em PR → send-message na filha
  (`delegate_message`) que rebase + re-verify.

### RF-003 — Tracking

- `refactor_plans` + `refactor_batches` (state
  `pending|ready|running|verifying|pr_open|merged|failed|blocked`,
  pr_url, conversation_id, attempts, verify_log_digest).
- UI: página do refactor — batch graph (cores por estado), % concluído,
  links PRs/filhas; ações pause/resume/cancel/retry-batch.
- `GET /api/v1/refactors`, `/{id}`, `/{id}/batches`, `POST /{id}/pause`
  `/resume` `/batches/{n}/retry`.

### RF-004 — Fora de escopo

- Merge automático · rebase automático de mainline sob o stack
  (Devin faz; v1: conflito → `blocked` + attention) ·
  grafos cross-repo · stacked PRs em GitLab/AzDO · edição do plano
  durante execução.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| POST | `/api/v1/refactors` | cria planner → `{plan_id, conversation_id}` |
| GET | `/api/v1/refactors` · `/{id}` | lista/detalhe + batch states |
| POST | `/api/v1/refactors/{id}/start` | após aprovação do plano |
| POST | `/api/v1/refactors/{id}/pause` · `/resume` | controle |
| POST | `/api/v1/refactors/{id}/batches/{n}/retry` | re-executa lote |

## 4. Data model

- `refactor_plans` (id, org, created_by, repos_json, goal, strategy,
  mode, max_concurrency, plan_json, status, coordinator_conversation_id).
- `refactor_batches` (id, plan_id, n, files_json, depends_on_json,
  description, risk, verify_cmd, state, pr_url, conversation_id,
  attempts, verify_log_digest, created/updated).

## 5. NFR / Security

- `perm:refactors.run` para criar/start; aprovação de plano exige role
  com `perm:refactors.approve` (default admin) — planner é LLM, plano é
  untrusted até revisão humana.
- Filhas herdam policies/quotas (SPEC-62); `max_concurrency` limitado
  por quota `concurrent_runs`.
- `verify_cmd` roda dentro do sandbox da filha — mesmo trust boundary
  de qualquer exec do agente.
- PRs de lote passam pelo pipeline normal de review (SPEC-140 os revisa
  se o repo estiver inscrito).

## 6. Task Plan

1. `refactor_plans`/`refactor_batches` + permissions + migrations.
2. Planner skill + dependency analysis (C# primeiro; heurística
   `using`/proj refs + fallback diretório).
3. `BatchExecutor`/coordinator + delegate + verify/fix loop + PR/lote.
4. Reconciliação merge/feedback + estados + UI graph.
5. `mode: stacked` (GitHub base-chain; native stacked API se disponível).
6. Testes: unit (topo-sort, overlap→stacked, state machine),
   integração repo fixture, golden OpenAPI.

## 7. Deliverables

Um goal de refatoração vira plano de lotes aprovável, executado por
agentes paralelos com verify gates, produzindo PRs ordenados com
progresso visível — "Refactor SDK" open-source.

## 8. Referências

- `docs/research/2026-11-competitive-gap-analysis.md` §2.2/§4 G5.
- OH blog `automating-massive-refactors-with-parallel-agents` +
  Large Codebase SDK whitepaper (conceitos apenas).
- Devin `work-with-devin/stacked-prs` (conceitos apenas).
- `docs/specs/62_managed_conversations_spec.md`,
  `52_github_app_scm_spec.md`.

## 9. Guardrails

- Plano sempre aprovado por humano antes de executar (hard gate).
- Overlap de arquivos entre lotes `independent` → planner rejeita ou
  força `stacked`/sequencial — nunca dois PRs tocando o mesmo arquivo.
- Nunca merge automático; humano mergeia cada lote (review SPEC-140
  ajuda, não decide).

## 10. DoD

- Plan gera lotes topológicos; execução respeita deps e concurrency
  (teste integração com fixture de 3 lotes).
- Verify gate reprova→fix→re-verifica; lote falho → `failed` sem
  desbloquear dependentes.
- Merge do lote N libera dependentes; `stacked` encadeia bases.
- Suite verde; migrations duplas; golden OpenAPI.
