# Eval Harness (`openhandsnet eval`)

SPEC-39: runner headless de tasks avaliáveis com fixtures versionadas e
scoring automático — gate de qualidade para mudanças de prompt/tools/
routing/contexto.

## Comandos

```bash
openhandsnet eval list                          # valida evals/tasks + suites
openhandsnet eval run smoke                     # roda suite evals/suites/smoke.yaml
openhandsnet eval run create-file               # roda task única
openhandsnet eval run smoke --parallel 4 --out results/
openhandsnet eval run smoke --mode recorded     # replay determinístico (sem LLM)
openhandsnet eval run smoke --record            # grava evidências p/ recorded
openhandsnet eval report results/smoke-XXXX/ --baseline results/prev/ --gate
```

## Formato de task (`evals/tasks/{name}.yaml`)

```yaml
name: fix-failing-test
repo: github.com/octocat/hello-world@sha   # opcional
task: "Fix the failing test in src/calc.py"
timeout: 10m
setup: []                                   # comandos pre-run no sandbox
assert:
  - type: exit_code                         # cmd executado pós-run
    cmd: "pytest -x"
    expect: 0
  - type: file_contains                     # lê arquivo do workspace
    path: /workspace/src/calc.py
    pattern: "def add"
  - type: output_contains                   # substring no transcript
    pattern: "all tests pass"
  - type: llm_judge                         # opcional — juiz LLM
    rubric: "solution is minimal and idiomatic"
tags: [bugfix, python]
```

## Fluxo do run

`POST /app-conversations` → poll `start-tasks/{id}` até **READY** →
`setup` via `POST /{id}/exec` → watch até status terminal → coleta
evidências (exec por assert `exit_code`, `GET /{id}/file` para
`file_contains`, transcript de eventos para `output_contains`, soma de
`TokenEvent` para custo) → `result.json` por task + `suite.json`.

## Scoring e gate

Score por task = média ponderada dos asserts objetivos
(`skipped` — ex.: `llm_judge` sem juiz configurado — fica fora do score).
Suite = média ponderada por `weight` da task. `--gate` + `--baseline`
sai com exit 2 quando uma task cai >5 pontos percentuais ou pass→fail.

## Modo recorded

`--record` num run live grava `evals/recordings/<name>-<hash>.json` com
as evidências congeladas (transcript + exit codes + conteúdo de
arquivos). `--mode recorded` reavalia os asserts sobre essa evidência
sem tocar a API — rerun idêntico, sem LLM. O hash cobre nome, instrução
e asserts da task: editar a task invalida o recording.

## Juiz LLM

`llm_judge` chama um endpoint OpenAI-compat configurado por env:

```bash
export OPENHANDSNET_EVAL_JUDGE_URL=http://localhost:11434/v1
export OPENHANDSNET_EVAL_JUDGE_MODEL=qwen2.5:7b
export OPENHANDSNET_EVAL_JUDGE_KEY=...   # se necessário
```

Sem config, o assert é marcado `skipped` — o `--gate` só considera
assertions objetivas por default (SPEC-39 §5).

## Segurança

- Host remoto exige `--allow-remote` (default: `http://localhost:8080`).
- Recordings contêm só evidências de asserts — nunca secrets.
- Suites: `smoke` (opt-in em PRs, futuro trigger `/eval`) e `nightly`
  (workflow próprio — gate humano, hard rule #6).
