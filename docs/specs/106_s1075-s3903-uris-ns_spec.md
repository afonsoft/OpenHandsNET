# SPEC — 106 · S1075 hardcoded URIs + S3903 namespaces

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `s1075-s3903-uris-ns` |
| Type | `Refactor` |
| Status | `Completed` |
| Ticket | SPEC-99 child of SPEC-89 |
| Scope | `src/ + cli/` |

## 1. User Story

**As a** mantenedor, **I want** o débito fatiado resolvido por área,
**so that** cada PR tem blast-radius pequeno e revisável.

## 2. Contexto / escopo

S1075 ×10 → config; S3903 → moves coordenados. Filho da SPEC-89 (deferred refactors).

## 3. Requisitos

- RF-001: corrigir as ocorrências da regra no escopo — sem mudar comportamento.
- RF-002: testes de regressão cobrindo áreas tocadas (existentes bastam se verdes).
- RF-003: won't-fix justificado documentado onde o design for intencional.
- RF-004: `dotnet format` + build 0 warnings + suite verde.

> **Fechamento 2026-11-06**: `csharpsquid:S3903` = 0 no rescan live;
> `S1075` caiu 10→1 — a última (`OhnApiClient` `"/"` delimiter) corrigida
> via char literal em PR #354 (build/teste verdes). Sem código adicional
> para os 9 sites anteriores — eliminados por refactors prévios.

## 4. Acceptance

- [x] Sonar deixa de reportar a regra no escopo (ou won't-fix documentado).
