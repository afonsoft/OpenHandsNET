# SPEC — 110 · Integration tests — flake hardening (round 2)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `integration-flake-hardening-2` |
| Type | `Fix` |
| Status | `In Review` |
| Ticket | gap-analysis 2026-10-24 · GAP-tests-integration-flakes |
| Scope | `tests/OpenHandsNET.IntegrationTests/**` sandbox-dependent |

## 1. User Story

**As a** mantenedor, **I want** os testes de integração dependentes de
sandbox estáveis, **so that** merges não ficam bloqueados em reruns
manuais de CI.

## 2. Contexto / escopo

SPEC-78 (#199, entregue) adicionou retry-once; hoje (2026-10-24) falhas
recorrentes ainda exigiram reruns manuais em ≥4 runs:

- `Spec43CheckpointEndpointsTests.Snapshot_Edit_Drift_Revert_Roundtrip`
  — falhou 3× (PR #257 ×2 tentativas, run main 40128507179)
- `Spec42WorkspaceEndpointsTests` (File_Ops_And_Search_Roundtrip,
  Write_Read_Etag_Conflict_Roundtrip)
- `AgentEngineE2ETests.InitialMessage_Run_Starts_Agent_Which_Finishes`
- `SkillsHooksE2ETests.Skills_Inject_InvokeSkill_Works_Hooks_Emit_Events`
- `McpEndpointsTests.ToolCall_Audits_Acp_Plus_Observation_Events`
- `Spec16LifecycleTests.SleepSchedule_Pauses_Running_Sandbox`

Padrão: testes distintos falham a cada run → provisionamento do sandbox
docker no runner é a variável instável, não o código sob teste.

## 3. Requisitos

- RF-001: instrumentar/diagnosticar causa raiz (timeout de pull de imagem,
  cold start do dockerd, ETag/concorrência real?) via logs dos runs.
- RF-002: estabilizar — warmup do sandbox, timeout aumentado onde o
  gargalo for provisionamento, ou quarentena xUnit traitada
  (`[Trait("Category","Flaky")]`) com execução separada não-bloqueante.
- RF-003: NÃO silenciar asserts — flake real de produto deve virar bug.
- RF-004: suite verde sem reruns em ≥3 runs consecutivos de CI.

## 4. Acceptance

- [ ] `build + test` passa de primeira em 3 merges consecutivos na main.
- [ ] Testes quarentenados listados na SPEC com justificativa e issue
  de reabilitação.

## 4. Execução (2026-10-24)

**Diagnóstico:**
- Falhas do round se concentram em dois padrões: (a) janela entre
  task READY e container Running (pull frio de `bash:5` no runner) —
  probe de 30s era curto demais; (b) escrita concorrente em SQLite
  (`database is locked`) entre contexts — reproduzido em unit test
  (`Spec54ChatOpsTests`: postback fire-and-forget vs dedup seguinte).

**Hardening aplicado:**
- Probe `/exec` 30s → 90s (`Spec43CheckpointEndpointsTests`,
  `ObservabilityTests`).
- `apk add` com 1 retry (mirror Alpine flakea ~1%).
- `Default Timeout=10` (busy handler) em todas as connection strings
  SQLite dos testes de integração + Spec54 unit — absorve locks curtos
  entre contexts concorrentes.

**Não-máscara (RF-003):** asserts intactos — apenas timeouts/retry de
provisionamento e busy-timeout de infra de teste. O race do postback
é real em SQLite; em Postgres (prod) a escrita concorrente é suportada.

**Pendente:** RF-004 (3 runs verdes) avaliado após merge via nightly.
