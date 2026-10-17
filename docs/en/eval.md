# Eval harness (SPEC-39)

`openhandsnet eval` — headless run of task fixtures against a real
backend, for prompt/tool regression.

## Commands

```bash
openhandsnet eval run evals/smoke.jsonl --url http://localhost:8080
openhandsnet eval report <run-id>
openhandsnet eval list
```

## Fixture format (JSONL)

```json
{"id": "smoke-hello", "prompt": "create hello.txt with 'hi'", "assert": [{"type": "file_contains", "path": "hello.txt", "contains": "hi"}]}
```

Assert types: `exit_code`, `file_contains`, `output_contains`,
`llm_judge` (OpenAI-compatible judge via env config).

## Flags

- `--parallel N` — concurrent tasks (default 1).
- `--gate baseline.json` — fail the run if score < baseline (CI gate).
- `--record` / replay mode — determinism via recorded evidence.

## Output

`result.json` per run + per-task scoring; `eval report` prints the
score table. `evals/` ships `smoke` and `nightly` suites; the nightly
CI workflow is a pending human gate.

Português: [../eval.md](../eval.md).
