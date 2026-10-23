# SPEC — 104 · S107 too many parameters

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `s107-many-params` |
| Type | `Refactor` |
| Status | `Draft` |
| Ticket | SPEC-99 child of SPEC-89 |
| Scope | `src/**/*.cs` |

## 1. User Story

**As a** mantenedor, **I want** o débito fatiado resolvido por área,
**so that** cada PR tem blast-radius pequeno e revisável.

## 2. Contexto / escopo

S107 ×29 — Introduce Parameter Object nos construtores god. Filho da SPEC-89 (deferred refactors).

## 3. Requisitos

- RF-001: corrigir as ocorrências da regra no escopo — sem mudar comportamento.
- RF-002: testes de regressão cobrindo áreas tocadas (existentes bastam se verdes).
- RF-003: won't-fix justificado documentado onde o design for intencional.
- RF-004: `dotnet format` + build 0 warnings + suite verde.

## 4. Acceptance

- [ ] Sonar deixa de reportar a regra no escopo (ou won't-fix documentado).
