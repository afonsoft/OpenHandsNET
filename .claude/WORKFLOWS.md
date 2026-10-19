# WORKFLOWS.md — Automação

## CI (`.github/workflows/ci.yml`) — push/PR para main

| Job | Conteúdo |
|---|---|
| `build-test` | restore → **lint (`dotnet format --verify-no-changes`)** → build Release → unit+client tests → integration tests → `check-env-drift.sh` |
| `contracts` | golden event fixtures (`--filter FullyQualifiedName~Golden`) |
| `docker` | build imagem `openhandsnet:ci` + `docker compose config -q` (2 profiles) |
| `sonarcloud` | gated `vars.SONAR_ENABLED == 'true'` — `dotnet-sonarscanner` + secret `SONAR_TOKEN` (org `afonsoft`, projeto `afonsoft_OpenHandsNET`) |

## Outros workflows

- `k8s-e2e.yaml` — `workflow_dispatch` + cron nightly 04:00 UTC (E2E k8s)
- `release.yml` — release pipeline

## Fluxo SDD (skills afonsoft)

`write-specs` → gate aprovação → `create-issues` → `execute-specs`/`tdd-spec`
→ `qa-analyst` → `code-review-and-quality` → PR → merge → arquivar SPEC em
`docs/specs/`. Orchestrador: `~/.claude/skills/orchestrator` (state em
`.claude/memory/orchestrator_*.md`).

## Hooks

Nenhum hook local configurado — controles via `permissions` +
`global-rules.md` + CI (computacional > prompt).
