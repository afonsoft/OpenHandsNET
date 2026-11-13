# SPEC — 152 · Benchmark adapters (Terminal-Bench / SWE-style evals)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `benchmark-adapters` |
| Type | `Feature / Eval infra` |
| Stack | `.NET 10 · sandbox runtime · batch runner` |
| Repository | `afonsoft/sandcastle` |
| Status | `Delivered` |
| Ticket | `GAP-BENCHMARKS` (docs/research/2026-11-competitive-gap-analysis.md §P2) |
| Depends on | `04` (conversations), `12` (git), `149` (router — medir por modelo), `144` (batch/parallel) |
| Reference | Terminal-Bench 2.0, SWE-bench Pro harnesses — conceitos apenas; NÃO copiar código benchmark |
| Touches | `src/Sandcastle.{Application,Infrastructure,Api}`, `cli/`, `tests/` |

## 1. User Story

**As a** maintainer do Sandcastle
**I want** rodar benchmarks públicos (Terminal-Bench, SWE-bench
Verified/Pro subsets) contra o agente real e ter um scorecard
reproduzível por modelo/versão
**So that** medimos o que o mercado mede — taxa de PR aceito,
terminal tasks resolvidas — e comparamos releases. Hoje não existe
nenhum harness de eval.

## 2. Scope (RF)

### RF-001 — Eval model

- `eval_suites` (builtin registradas): `terminal-bench-2` (tasks
  containerizadas com checker script), `swe-lite` (subset curado
  `n≤20` de SWE-bench-like para smoke), `custom` (jsonl de tasks).
- `eval_runs`: `{id, suite, n_tasks, model/route, status,
  resolved_n, failed_n, error_n, score_pct, stats_json, created_at,
  finished_at}` + `eval_results` por task `{task_id, verdict
  (resolved|failed|error|timeout), duration_s, tokens, cost,
  session_id, log_digest}`.
- Task contract `eval_task_v1`: `{task_id, repo|image, setup_cmds[],
  prompt, checker (cmd|path), timeout_s, metadata}`.

### RF-002 — Runner

- `EvalRunner` (worker sobre a infra de conversations/sandbox):
  por task → sandbox efêmero → setup → agente recebe `prompt` (modo
  `eval` — sem steering; `Eval:MaxTurns`, `Eval:TaskTimeout=1800`) →
  checker roda no sandbox → verdict gravado. Paralelismo
  `Eval:MaxConcurrency=4` (quota org aplica).
- Agente eval usa a MESMA conversa/tool pipeline (não um harness
  separado) — flag `eval_mode` no start-task interno que desativa
  ChatOps/notifications e marca `agent_type: eval`.
- `POST /api/v1/evals` `{suite, model?, route_override?, n?}` → 202;
  `GET /evals`, `/{id}`, `/{id}/results`; `POST /{id}/cancel`.
- CLI: `sandcastle eval run|status|report` — `report` gera
  scorecard md/json (paridade com leaderboard format: resolved%).

### RF-003 — Suites builtin

- `terminal-bench-2`: adapter lê o dataset público (HuggingFace/
  GitHub release — `Eval:DatasetCacheDir` com download manual ou
  `eval datasets fetch`; task → image + checker).
- `swe-lite`: subset estático embutido (`bench/swe-lite.jsonl` no
  repo — curado, ~20 tasks variadas) para smoke rápido.
- Scorecard comparativo: `GET /api/v1/evals/scorecard?suite=X` —
  últimas N runs agrupadas por model/route com resolved%.

### RF-004 — Guardrails de custo

- Eval é caro: `perm:evals.run` (admin default); hard caps
  `Eval:MaxTasks=200`, `MaxConcurrency`, `MaxDailyCostUsd` por org;
  dry-run `POST /evals/estimate` → custo estimado (tokens médios ×
  tasks × modelo).

### RF-005 — Fora de escopo

- Submissão a leaderboards públicos · corpus licensing/hosting do
  benchmark (usuário fornece dataset — nós só adaptamos) ·
  benchmark de review/autofix (meta-eval) · fuzzing corpora —
  fase 2.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| POST | `/api/v1/evals` | `{suite, model?, n?, route_override?}` |
| GET | `/api/v1/evals` · `/{id}` · `/{id}/results` | |
| POST | `/api/v1/evals/{id}/cancel` | |
| POST | `/api/v1/evals/estimate` | `{suite, n, model}` → est. cost |
| GET | `/api/v1/evals/scorecard?suite=` | resolved% por modelo |

## 4. Data model

- `eval_runs`, `eval_results` — migrations duplas.

## 5. NFR / Security

- Tasks rodam em sandbox descartável com egress allowlist do
  blueprint; checker é código untrusted → mesmo trust boundary.
- Datasets baixados ficam em volume cache dedicado (`eval-cache`),
  nunca no repo do usuário.
- `eval_mode` desativa side-effects externos: sem ChatOps, sem
  review post, sem autofix triggers (hermetic).

## 6. Task Plan

1. Entities + contract `eval_task_v1` + migrations.
2. `EvalRunner` + eval_mode na start-task + checker exec.
3. Adapters: swe-lite (embedded jsonl) + terminal-bench-2 (dataset
   fetch/cache) + custom.
4. Endpoints + estimate + scorecard + CLI verbs.
5. Testes: unit (contract parse, verdict, dedup, estimate),
   integração (suite fake 3 tasks → run completo), golden.

## 7. Deliverables

`eval run` executa benchmarks reais hermeticamente no sandbox e
produz scorecard resolved% — medindo o que o mercado mede.

## 8. Referências

- `docs/research/2026-11-competitive-gap-analysis.md` §P2
  (Terminal-Bench 2.0, SWE-Pro, PR acceptance rate).
- `docs/specs/144_batch_refactor_spec.md` (parallel orchestration),
  `149_model_router_spec.md` (model axis).

## 9. Guardrails

- Eval sessions são hermetic (zero side-effects fora do sandbox).
- Nunca commitar datasets de terceiros ao repo (licença) — só
  adapters + o subset `swe-lite` próprio/curado.
- Estimate antes de run >`Eval:EstimateGateTasks=25`.

## 10. DoD

- `eval run swe-lite` → run completo → scorecard com resolved% +
  results por task (fixtures).
- Terminal-bench adapter parseia dataset real (sample em tests).
- Hermeticidade: nenhum side-effect externo durante eval.
- Suite verde; migrations duplas; golden.
