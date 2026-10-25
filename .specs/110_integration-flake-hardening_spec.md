# SPEC — 110 · Integration tests — flake hardening (round 2)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `integration-flake-hardening-2` |
| Type | `Fix` |
| Status | `Approved` |
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
