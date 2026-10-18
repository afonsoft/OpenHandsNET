# Orchestrator Sessions

## Session — 2026-10-15

**Scope**: Final delivery wave — SPECs 28, 30, 31, 34 merged earlier this session; SPEC-26 UI/UX (PR #73) merged this run.

**Decisions**:
- SPEC-26 merged via squash `fb5eae1`; issue #55 closed automatically.
- Harness audit: created thin `AGENTS.md` pointing to `CLAUDE.md` (Phase 2 gap P4).
- Orphaned worktrees ohn-spec26/28/30/31/34 + 10 merged branches removed (gap check: no dead branches).

**Delivered**:
- PR #73 merged — themes (`--ohn-*`, dark/light/system, zero-flash), vendored SVG icons, Markdig+sanitized chat, tool-call cards, Ctrl+K palette, toasts, offline banner, sidebar v2, model selector, agent-mode toggle.
- `orchestrator_stats.md` + `AGENTS.md` on main (`e6fdb71`, `~e6fdb72`).

**Remaining**: none — all 10 SPECs (18–34) merged, 0 open issues, 0 open PRs.

**Lessons**:
- Shared `MockLlmProvider` queue across parent/subagent loops = nondeterministic CI; branch the mock on the child system prompt.
- Scoped `DbContext` under concurrent reconcile polling needs a `SemaphoreSlim` gate (`DockerSandboxService`).
- Upstream enterprise/cloud is Polyform — reimplement concepts, never port.

## Session — SPEC-50 + SPEC-51 merged (2026-10-18)

**Task**: continue `absorbed-swift` — sequential 49→merge→50→merge→51 (49 was already merged; 50 was uncommitted in worktree `ohn-spec50`).

**Delivered**:
- PR #121 — SPEC-50 BYOR LiteLLM (merge `800ee20`): `ILiteLlmAdminClient` + `ByorKeyService` (DataProtection ciphertext, one active key per user via unique filtered index), 4 endpoints no longer 403, runner fail-closed (`byor_model_rejected`/`byor_key_missing` → diagnostic + run Error), provider sends `Bearer <vkey>` + `X-LiteLLM-Key`, `ModelSelector` filters by permitted, real `deploy/litellm.yaml` + pg init + enterprise compose + docs pt/en.
- PR #122 — SPEC-51 OpenAPI contract (merge `9e84edd`): `AddOpenApi("v1")` + `/openapi/v1.json` (143 paths), golden snapshot `Golden/openapi-v1.json` + `OPENHANDSNET_UPDATE_GOLDEN=1` refresh, Scalar explorer opt-in (`Api:OpenApiExplorer`), docs api/README pt+en.
- Issues #116, #117 closed; SPECs 50/51 → `Done` on main (`65d4e50`, `12a47ad`). **Enterprise wave (48–51) complete.**

**Fixes worth remembering**:
- Never hand-edit EF model snapshots without a migration — SPEC-50 worktree had `byor_keys` in snapshot but no `CreateTable` → runtime `no such table`. Fix: revert snapshot, regenerate migration via `dotnet ef --startup-project src/OpenHandsNET.Api` (Infrastructure lacks EFCore.Design).
- SQLite can't `ORDER BY DateTimeOffset` — companion `*Ticks` column (repo convention).
- `MapFallbackToFile` swallows unmapped routes → opt-in endpoint tests must assert on body markers, not status.
- NuGet ≥7-day rule: check publish dates via `v3/registration5-gz-semver2` index (e.g. chose OpenApi 10.0.12 over 5-day-old 10.0.13).
