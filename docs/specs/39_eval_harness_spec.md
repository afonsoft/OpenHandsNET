# SPEC — 39 · Eval Harness (`openhandsnet eval`)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `eval-harness` |
| Type | `Feature / Tooling (Quality)` |
| Stack | `.NET 10 · CLI (Spectre) · headless runner · fixtures` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` (MVP: `eval run|report|list`, `--parallel`, asserts `exit_code`/`file_contains`/`output_contains`, `llm_judge` via juiz OpenAI-compat env, `evals/` smoke+nightly, `result.json`, `--gate` baseline, modo `recorded` por replay de evidências; workflow nightly = gate humano pendente) |
| Ticket | #78 |
| Depends on | `06`/`31` (CLI), `09` (agent engine), `24` (activity/events), `22` (context), `28` (subagents) |
| Touches | `cli/OpenHandsNET.Cli` (`eval` command), `tests/` (fixtures), CI nightly |

## 1. User Story

**As a** mantenedor do engine/prompts
**I want** um runner headless de tasks avaliáveis (`openhandsnet eval`)
com fixtures versionadas e scoring automático
**So that** regressões de prompt, tools, routing e contexto são medidas
em CI — alinhado ao modelo de benchmarks WebArena/OSWorld do upstream
(`open-operator`, conceitos apenas).

**Contexto:** mudanças em system prompt, tool schemas, condenser e
router hoje não têm medição objetiva — só testes unitários. Um harness
de eval permite gate de qualidade antes de merge e comparação
baseline↔candidate.

## 2. Scope (RF)

### RF-001 — Formato de task

```yaml
# evals/tasks/{name}.yaml
name: fix-failing-test
repo: github.com/octocat/hello-world@sha   # ou fixture local tar
task: "Fix the failing test in src/calc.py"
timeout: 10m
setup: []               # comandos pre-run no workspace
assert:
  - type: exit_code     # cmd run no sandbox pós-run
    cmd: "pytest -x"
    expect: 0
  - type: file_contains
    path: src/calc.py
    pattern: "def add"
  - type: llm_judge      # opcional — role Summarizer avalia rubrica
    rubric: "solution is minimal and idiomatic"
tags: [bugfix, python]
```

### RF-002 — `openhandsnet eval`

```
openhandsnet eval run <suite|task> [--parallel N] [--model X]
                     [--agent-profile dev] [--out results/]
openhandsnet eval report results/ [--baseline prev/]
openhandsnet eval list
```

- Runner headless: cria conversa via API local/embutida, roda task num
  sandbox efêmero, coleta assertions → `result.json` por task
  (`{score, events_summary, tokens, duration, artifact_refs}`).
- Provider de ambiente: `local` (API embutida in-process + sandbox
  Docker) — default para CI; `remote` (aponta para instância —
  `OPENHANDSNET_URL`+token).
- `--parallel N` com semaphore; isolamento: sandbox por task.

### RF-003 — Scoring

Score 0..1 por task: assertions weighted; `llm_judge` opcional via
role `Summarizer` da SPEC-23 (rubrica → score). Suite score =
média; report compara baseline (`--baseline`) com delta por task e
regressions flagged (>5% drop → exit non-zero com `--gate`).

### RF-004 — Suites + CI

- `evals/suites/smoke.yaml` (5–8 tasks rápidas, <15min) — PR opt-in
  (`/eval` comment trigger, futuro).
- `evals/suites/nightly.yaml` — GH Action nightly com results +
  artifacts publicados (workflow próprio — gate humano para criar
  o workflow, hard rule #6).
- Custos: eval usa role/model configurável; token usage registrado no
  report (soma de `TokenEvent` do run).

### RF-005 — Determinismo

Seed/temperature 0 nos evals; `recorded` mode (LLM responses
cacheadas por hash do prompt) para smoke suite determinística —
`--mode live|recorded` (recorded = fixture de respostas versionada,
nunca committar respostas com secrets — rubricas e outputs públicos).

## 3. API Contract

CLI consome só contratos públicos existentes (`/app-conversations`,
events stream, sandbox exec). Nenhum endpoint novo no MVP;
`eval report` é local (lê `result.json`).

**Divergência (implementação):** o contrato "sandbox exec" presumido
não existia — adicionado `POST /api/v1/app-conversations/{id}/exec`
({command, timeout_s} → {exit_code, stdout, stderr, timed_out}, cwd
`/workspace`, mesmo guard de sandbox-running do `GET /file`) para os
asserts `exit_code`/`setup`.

## 4. Data model

Sem migration. Artifacts do eval em `evals/results/<run>/` (gitignored;
grandes → artifact store da SPEC-34 quando `s3` ativo).

## 5. NFR / Security

- Eval nunca roda contra produção; `remote` mode exige flag
  `--allow-remote` + env de instância dedicada.
- Fixtures não contêm secrets; tasks que precisam de credencial usam
  secrets by-ref (SPEC-33) do ambiente de CI.
- `llm_judge` marcado no report (não-determinístico) — gate só sobre
  assertions objetivas por default.

## 6. Task Plan

1. Schema `evals/tasks/*.yaml` + parser + validator (`eval list`
   valida).
2. Runner: create conversation → wait ready → assert → collect.
3. Scoring + `report` (table + JSON + baseline diff).
4. `recorded` mode (prompt-hash cache).
5. Smoke suite inicial (5 tasks cobrindo edit/run-test/git_commit/
   search/web_fetch) + doc `docs/eval.md`.
6. (follow-up, aprovação humana) workflow nightly.

## 7. Deliverables

`cli/OpenHandsNET.Cli/Commands/EvalCommand.cs`,
`src/OpenHandsNET.Eval/` (runner+scoring — lib separada do CLI p/
reuse), `evals/tasks`, `evals/suites`, docs, testes do parser/scorer.

## 8. Referências

- `OpenHands/open-operator`: benchmarks WebArena/OSWorld/ClawBench +
  capabilities catalog (conceitos — SPEC-32 §2).
- SWE-bench-style task+assert format (conceito).

## 9. Guardrails

- Eval não altera código de produção do engine — é consumidor.
- Nenhum eval em CI de PR por default (custo) — nightly/smoke opt-in.
- Fixtures públicas apenas; sem dados de clientes.
- Não criar `.github/workflows/` sem aprovação humana (hard rule).

## 10. DoD

- `eval run smoke` local: 5 tasks passam contra build atual;
  `result.json` + report gerados.
- Regression sintética (quebrar prompt de propósito em branch teste)
  derruba score → `--gate` exit non-zero.
- `recorded` mode: rerun sem LLM = mesmo resultado.
- `dotnet test` verde.
