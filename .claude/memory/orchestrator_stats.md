# orchestrator_stats

## Session

- **started_at**: `2026-10-19`
- **current_phase**: `Phase 7 (status check)`
- **repository**: `afonsoft/OpenHandsNET`
- **branch**: `main`
- **last_updated**: `2026-10-19`

## Project Context

- **stack**: `.NET 10 / Blazor WASM / EF Core / Docker`
- **test_command**: `dotnet test`
- **build_command**: `dotnet build OpenHandsNET.slnx -c Release`
- **lint_command**: `dotnet format --verify-no-changes`
- **coverage_target**: `n/a` (suites: unit + client + integration + agent)

## SPEC DAG Status

| SPEC | Issue | PR | Status |
|---|---|---|---|
| 18-25, 27, 32 | — | #58-#60 | merged |
| 29 permissions | #62 | #67 | merged |
| 30 memory | #63 | #68 | merged |
| 33 hardening | #65 | #69 | merged |
| 28 subagents | #61 | #70 | merged |
| 34 deltas+artifacts | #66 | #71 | merged |
| 31 CLI parity | #64 | #72 | merged |
| 26 UI/UX | #55 | #73 | merged `fb5eae1` |
| 35-47 | — | #74-#113 | merged |
| 48-51 (enterprise wave) | #114-#117 | #118-#122 | merged |
| 52-64 (waves A/B/C gap analysis) | — | #123-#145 | merged |

> Status check 2026-10-19: `dotnet test` na main — 851/851 verde
> (Unit 587 · Integration 174 · Client 80 · SandboxAgent 10).
> 0 issues/PRs abertos. SonarQube nunca integrado; última
> gap-analysis formal = SPEC-32/waves 52-64 (todas entregues).

## Decisions Log

- SPEC-28 test race: `MockLlmProvider` shared queue parent↔child —
  fixed with `SubagentAwareMockProvider` branching on system prompt.
- SPEC-26 DbContext flake: `DockerSandboxService` read paths gated by
  `SemaphoreSlim` (`SleepSchedule_Pauses_Running_Sandbox`).
- Upstream enterprise/cloud = Polyform — conceitos reimplementados,
  nunca portados (hard rule).
