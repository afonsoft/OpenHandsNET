# orchestrator_stats

## Session

- **started_at**: `2026-10-15 12:00:00`
- **current_phase**: `Phase 8 (done)`
- **repository**: `afonsoft/OpenHandsNET`
- **branch**: `main`
- **last_updated**: `2026-10-15`

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
| 26 UI/UX | #55 | #73 | **merged `fb5eae1`** |

## Decisions Log

- SPEC-28 test race: `MockLlmProvider` shared queue parent↔child —
  fixed with `SubagentAwareMockProvider` branching on system prompt.
- SPEC-26 DbContext flake: `DockerSandboxService` read paths gated by
  `SemaphoreSlim` (`SleepSchedule_Pauses_Running_Sandbox`).
- Upstream enterprise/cloud = Polyform — conceitos reimplementados,
  nunca portados (hard rule).
