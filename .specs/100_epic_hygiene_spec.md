# SPEC — 100 · Epic hygiene — auditar #146/#148

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `epic-hygiene` |
| Type | `Chore` |
| Status | `Approved` |
| Ticket | gap-analysis 2026-10-23 |
| Depends on | SPEC-96 |
| Touches | issues #146, #148 |

## 1. User Story

**As a** mantenedor, **I want** epics antigos auditados contra o
estado real, **so that** o backlog não carrega zombies.

## 2. Requisitos

- RF-001: #146 (gap-analysis 19/10) — verificar filhos entregues;
  fechar se completo ou listar resto.
- RF-002: #148 (SPEC-66 quality gates) — verificar acceptance:
  .editorconfig+format em CI ✓?, SonarCloud ativo ✓ (checks passam),
  SONAR_TOKEN/SONAR_ENABLED — fechar ou listar gap real.
- RF-003: labels status corretas (`done`/`in-progress`) nos abertos.

## 4. Acceptance

- [ ] #146/#148 fechados ou com comentário de gap restante.
