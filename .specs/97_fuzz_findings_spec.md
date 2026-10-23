# SPEC — 97 · Schemathesis fuzz findings — auth anônima + 500s

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `fuzz-findings` |
| Type | `Security / robustness` |
| Status | `Approved` |
| Ticket | #218 |
| Depends on | SPEC-80 (openapi fidelity) entregue |
| Touches | endpoints sem auth declarada, handlers 500 |

## 1. User Story

**As a** mantenedor, **I want** corrigir os achados do fuzz nightly,
**so that** nenhuma rota sensível responde sem auth e nenhum input
malformado produz 500.

## 2. Contexto

Issue #218 (aberta 20/10): schemathesis reportou 35 ops
`ignored_auth` + 10 `not_a_server_error` + rotas ambíguas. Baseline
do nightly exclui essas classes — dívida real congelada.

## 3. Requisitos

- RF-001: audit das 35 ops `ignored_auth` — classificar intencional
  (health/public) vs gap; exigir `RequireAuthorization` onde devido.
- RF-002: corrigir os 10 `not_a_server_error` — input validation → 4xx.
- RF-003: remover exclusions do nightly à medida que fixes mergeam;
  nightly deve rodar com checks completos quando zero.
- RF-004: testes de contrato p/ cada rota corrigida.

## 4. Acceptance

- [ ] 0 `ignored_auth` não-justificado; 0 `not_a_server_error`.
- [ ] Nightly verde com checks completos (sem exclusions por classe).
