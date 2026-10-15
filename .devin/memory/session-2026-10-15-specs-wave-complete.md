# Session Memory — 2026-10-15 — OpenHandsNET SPECs wave complete

## Scope
Closed the final improvement wave: SPECs 18–34 all merged to main.
Orchestrator skill run to completion (phases -1..8). 0 open issues/PRs.

## Key state
- Repo: `afonsoft/OpenHandsNET`, main HEAD ~`e6fdb72` (AGENTS.md added).
- PRs merged this wave: #67 SPEC-29, #68 SPEC-30, #69 SPEC-33,
  #70 SPEC-28 (`3091d49`), #71 SPEC-34 (`03ff76a`), #72 SPEC-31 (`6f15342`),
  #73 SPEC-26 (`fb5eae1`). Issues #55, #61–66 closed.
- State files: `.claude/memory/orchestrator_stats.md`,
  `.claude/memory/orchestrator_sessions.md`, `AGENTS.md` (thin → CLAUDE.md).
- Worktrees `ohn-spec{26,28,30,31,34}` removed; merged branches deleted.

## Decisions
- All spec files use `.specs/NN_name_spec.md` with `Status: Done (PR #N)`.
- Upstream enterprise/cloud is Polyform → reimplement concepts, never port.
- Deferred backlog (SPEC-32 Drafts): runtime-api split, Redis backplane,
  k8s+Sysbox, wildcard host routing, eval harness.

## Lessons / gotchas
- `MockLlmProvider` shared queue races across parent/subagent loops —
  branch test mocks on the child system prompt ("subagent inside
  OpenHandsNET"); fix in `tests/OpenHandsNET.UnitTests/Agent/Spec28SubagentsTests.cs`.
- Scoped `AppDbContext` under concurrent `GetAsync`/`SearchAsync` reconcile
  polls → `SemaphoreSlim _dbGate` in `DockerSandboxService`.
- bUnit strict JS interop → wrap best-effort JS (scroll, theme) in broad
  `catch (Exception)` in components (`ChatPane.razor`).
- Namespace collision: sibling `Infrastructure.Sandbox`/`Application.Sandbox`
  shadows `Domain.Sandbox` — qualify `Domain.Sandbox.Sandbox` or alias
  `RuntimeSandbox`.
- CI flakes seen: `sandbox_not_running` (Docker pressure → rerun passes),
  subagent script race (fixed, deterministic).

## Commands
- Build: `dotnet build OpenHandsNET.slnx -c Release`
- Test: `dotnet test` per project (unit 369+, client 39+, integration ~105)
- Migration: two contexts (SQLite + Postgres) — generate both.
