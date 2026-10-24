# SPEC — 102 · S3776 cognitive complexity — infra/agent

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `s3776-infra-agent` |
| Type | `Refactor` |
| Status | `Completed` |
| Ticket | SPEC-99 child of SPEC-89 |
| Scope | `src/OpenHandsNET.Infrastructure/{Agent,Tools}/**/*.cs` |

## 1. User Story

**As a** mantenedor, **I want** o débito fatiado resolvido por área,
**so that** cada PR tem blast-radius pequeno e revisável.

## 2. Contexto / escopo

S3776 ×~35 em AgentRunner/BuiltinTools; extrair decisões p/ métodos nomeados. Filho da SPEC-89 (deferred refactors).

## 3. Requisitos

- RF-001: corrigir as ocorrências da regra no escopo — sem mudar comportamento.
- RF-002: testes de regressão cobrindo áreas tocadas (existentes bastam se verdes).
- RF-003: won't-fix justificado documentado onde o design for intencional.
- RF-004: `dotnet format` + build 0 warnings + suite verde.

## 4. Acceptance

- [ ] Sonar deixa de reportar a regra no escopo (ou won't-fix documentado).
