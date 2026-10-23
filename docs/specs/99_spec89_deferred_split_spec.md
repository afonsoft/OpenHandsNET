# SPEC — 99 · SPEC-89 deferred refactors — split por área

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `spec89-deferred-split` |
| Type | `Refactor / planning` |
| Status | `Approved` |
| Ticket | gap-analysis 2026-10-23 |
| Depends on | SPEC-96 (counts live) |
| Touches | `.specs/89_*` → SPECs filhos |

## 1. User Story

**As a** mantenedor, **I want** a dívida deferida da SPEC-89 fatiada
em SPECs por área, **so that** S3776×74/S3218×2/S107×29/S127×3/
S1075×10/S3903 viram trabalho executável em vez de blob.

## 2. Requisitos

- RF-001: um SPEC filho por regra/área: S3776 (cli/, infra/agent,
  endpoints), S107 (construtores god), S3218 (shadowing settings/cli),
  S127 (AgentRunner loop), S1075 (uris), S3903 (namespaces).
- RF-002: cada filho com blast-radius + testes de regressão definidos.
- RF-003: won't-fix justificado onde o design for intencional.
- RF-004: SPEC-89 vira índice dos filhos (ou arquivada).

## 4. Acceptance

- [ ] SPECs filhos Draft+ criados; SPEC-89 atualizada.
