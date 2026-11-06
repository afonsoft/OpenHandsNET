# SPEC — 103 · S3776 cognitive complexity — api endpoints

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `s3776-endpoints` |
| Type | `Refactor` |
| Status | `Completed → docs/specs` |
| Ticket | SPEC-99 child of SPEC-89 |
| Scope | `src/OpenHandsNET.Api/Endpoints/*.cs` |

## 1. User Story

**As a** mantenedor, **I want** o débito fatiado resolvido por área,
**so that** cada PR tem blast-radius pequeno e revisável.

## 2. Contexto / escopo

S3776 ×~14; extrair lambdas de endpoint p/ handlers. Filho da SPEC-89 (deferred refactors).

## 3. Requisitos

- RF-001: corrigir as ocorrências da regra no escopo — sem mudar comportamento.
- RF-002: testes de regressão cobrindo áreas tocadas (existentes bastam se verdes).
- RF-003: won't-fix justificado documentado onde o design for intencional.
- RF-004: `dotnet format` + build 0 warnings + suite verde.

## 4. Acceptance

- [x] Sonar deixa de reportar a regra no escopo (ou won't-fix documentado).

## Entrega (2026-11-06)

- Escopo real: 17 sites `Map*Endpoints` em 17 arquivos + `ProgramParts`.
- ~95 lambdas inline extraídas p/ handlers nomeados em 4 PRs: #357 (Ticket/Knowledge/ChatOps + ProgramParts/SandboxService/BuiltinSpecs), #358 (Sandbox/Auth/Git/Share/BrowserProfile/Acp), #359 (Admin/Keys/Scm/Automation/Health), #360 (Org CC105 / Scim CC102 / AppConversation CC75+24), #362 (batch5 rescan follow-up — lambdas pequenos restantes em Admin/Automation/Scm + transformers AddOpenApiDoc; rescan live: S3776=0 no escopo).
- Tags OpenAPI preservadas via `.WithTags("OpenHandsNET.Api")` onde o grupo não define tag — golden contract byte-idêntico.
- Heurística local (decision points + nesting): 0 métodos ≥15 em `Endpoints/*.cs`.
