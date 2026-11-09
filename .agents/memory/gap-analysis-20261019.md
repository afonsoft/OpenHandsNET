# Gap Analysis — 2026-10-19 (run state)

## Phase 0 — Preconditions

- repo: `afonsoft/OpenHandsNET` · branch `main` @ `ca44522` · clean tree · no submodules
- `gh` auth: OK (afonsoft) · remote origin OK
- sibling skills: write-specs ✓ create-issues ✓ orchestrator ✓
- resume marker: **Phase 6 done — Issues created; SPECs Approved; awaiting Phase 7 handoff/execution**

## Phase 5–6 outcome (2026-10-19)

- Gate approved by user → SPECs 65/66/67 set to `Approved`.
- Issues: Epic **#146** (`epic`+`todo`); slices **#147** SPEC-65, **#148** SPEC-66, **#149** SPEC-67 (`slice`+`todo`).
- SPEC-66 partially blocked: Sonar part needs `SONAR_HOST_URL`/`SONAR_TOKEN` + project key (lint part unblocked).
- SPEC-67 scope: minimal variant (ADR thin-harness + docs/architecture + CONTEXT.md).

## Source inventory

| Source | Status |
|---|---|
| `.specs/` (67 files, NN_ convention — collect script pattern `SPEC-*` does not match) | present |
| `docs/` | present |
| `docs/architecture/` | **absent** |
| `.claude/memory/` | present |
| `.claude/CONTEXT.md` · `.claude/MEMORY.md` · `.claude/rules/` · `.claude/agents/` · `.claude/settings.json` | **absent** |
| CLAUDE.md · AGENTS.md · README.md | present |
| CI: `ci.yml` (build+test+golden) · `release.yml` · `k8s-e2e.yaml` (dispatch+nightly) | present |

## Candidates & verdicts

| Key | Verdict | Evidence |
|---|---|---|
| GAP-documentation-specs-archive | **CONFIRMADO** | `.specs/README.md` + orch Phase 5.8 demandam `docs/specs/`; ausente, 62 SPECs Done/Delivered ainda em `.specs/` |
| GAP-automation-sonarqube-gate | **CONFIRMADO** | orch Phase 2 prescreve `/sonarqube-autofix`; zero integração (sem `sonar-project.*`, sem step CI, sem issues); scanner instalado sem `SONAR_HOST_URL`/`TOKEN`/key |
| GAP-automation-lint-ci | **CONFIRMADO** | `orchestrator_stats.md` declara `lint_command: dotnet format --verify-no-changes`; `ci.yml` sem step de lint; `.editorconfig` ausente |
| GAP-documentation-harness-structure | **CONFIRMADO** (P4; escopo a validar — thin harness foi decisão consciente 2026-10-15) | checklist orch Phase 2: `.claude/settings.json`, `rules/`, `agents/`, `CONTEXT.md`, `MEMORY.md`, `README.md`, `docs/architecture/` — todos ausentes |
| GAP-documentation-spec-metadata | **CONFIRMADO** | colisão 52×2 (`52_github_app_scm` + `52_nextgen_improvements`) e 53×2; `53_enterprise_sandbox` sem `Status`; SPECs research 27/32/52-nextgen `Approved` → reenfileiradas pelo orchestrator |
| event-model-v1 | REJEITADO | `src/OpenHandsNET.Domain/Events/EventBase.cs`, discriminadores `kind`, `parent_id` |
| start-task-async / selected_repository | REJEITADO | `Domain/Conversations/AppConversationStartTask.cs`; `Client/Services/OpenHandsNetApiClient.cs:37`; `Api/Endpoints/AcpEndpoints.cs:45` |
| secrets-sha256-masked | REJEITADO | `Infrastructure/Identity/ApiKeyService.cs:48,100,169-170` (`SHA256` + `KeyHash`) |
| ef-migrations-startup | REJEITADO | `Infrastructure/Persistence/DatabaseMigrator.cs` + `Api/Program.cs` |
| tracked-logs | REJEITADO | `.gitignore:23-24` (`logs/`, `*.log`); `git ls-files` sem `.log` |
| k8s-e2e-ci | REJEITADO | `k8s-e2e.yaml` `workflow_dispatch` + cron nightly |
| observability | REJEITADO | Serilog/OTel em `Api` + `SupportBundleService` |
| fontes-de-verdade | REJEITADO | `docs/reference/upstream-baseline.md`, `migration-plan/research/api-inventory.md` presentes |

## Priorities

| Key | Impact | Urgency | Effort | Priority |
|---|---|---|---|---|
| sonarqube-gate | alto (P1/P2 nunca auditados) | média | média (precisa credencial) | **P1** |
| lint-ci | médio | média | baixa | P2 |
| specs-archive | baixo (higiene) | baixa | baixa | P3 |
| spec-metadata | baixo-médio (reenfileira specs no orchestrator) | média | baixa | P3 |
| harness-structure | baixo (P4) | baixa | baixa | P4 |

## Draft SPECs (Phase 5 — GATE PENDING)

- `.specs/65_specs_hygiene_spec.md` — specs-archive + spec-metadata
- `.specs/66_quality_gates_automation_spec.md` — lint-ci + sonarqube-gate (depende de credenciais Sonar)
- `.specs/67_harness_structure_spec.md` — harness-structure (escopo mínimo a validar)

## Test evidence

`dotnet test` @ `ca44522`: **851/851 pass** (Unit 587 · Integration 174 · Client 80 · SandboxAgent 10).

---

# Run 2 (2026-10-19, ~23:30 UTC) — layout parity open-webui

## Trigger
Pipeline architecture (Phase 5 handoff) + pedido do usuário: analisar
layout de `afonsoft/open-webui` (Open-WebUI+Code) × OpenHandsNET.Client.

## Sources
- .specs/ (70–74 sonar), docs/architecture/ (overview, AD-0001, .mmd/.svg,
  archify json+html — entregues nesta sessão, PR #189), CLAUDE.md, memory.

## Candidatos → vereditos
- GAP-architecture-layout-shell-mobile **CONFIRMADO** → SPEC-75 → #193
- GAP-architecture-chat-run-ux **CONFIRMADO** → SPEC-76 → #194
- GAP-architecture-ide-workbench **CONFIRMADO** → SPEC-77 → #195
- sidebar folders/pinned/archived — REJEITADO (valor baixo agora)
- CallOverlay/anexos/voz — REJEITADO (fora de escopo)
- auth-gate no MainLayout — REJEITADO (auth é server-side/API keys)

## Gate → execução
Usuário aprovou ("sim. e pode executa-ça") → Epic #192 + slices
#193-195 + implementação imediata.

- **PR #196** (`feat/spec75-shell-mobile`): SPEC-75 completo — drawer
  mobile (scrim/Escape/role=dialog), topbar mobile, expand no rodapé da
  rail, skip-link, AttentionFeed dropdown (+2 testes bUnit) — e SPEC-76
  parcial (status pill aria-live + git chip→diff tab).
- Pendente SPEC-76: RunActivityFeed collapse, SubConversationPanel
  (gated a `parent_id` na API). SPEC-77 inteira pendente (#195).

## PRs abertos ao fim do run
#189 (docs/architecture), #190 (signature guard test), #191
(download-artifact SHA), #196 (SPEC-75/76).
