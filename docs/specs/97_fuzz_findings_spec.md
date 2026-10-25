# SPEC — 97 · Schemathesis fuzz findings — auth anônima + 500s

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `fuzz-findings` |
| Type | `Security / robustness` |
| Status | `Completed` |
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

## Resultado — 2026-10-24

- **FallbackPolicy** global (`Program.cs`): endpoints sem `AllowAnonymous`
  exigem usuário autenticado — fecha os 35 `ignored_auth` de uma vez.
- **Anônimos intencionais**: health/ready (probes), `/api/v1/config`,
  `/api/v1/user` (stub OSS), openapi+json (fuzz), scalar, SPA fallback,
  static assets, `/framework-assets`, share links, auth/device flows,
  `webhooks/mcp-oauth-state` (callback MCP).
- **Webhook ingress** (`events/{id}`, `conversations`): agora exige
  Bearer sk-* — decisão explícita (antes eram escritas anônimas).
- **AmbiguousMatch**: `/api/v1/secrets*` tinha 2 donos (stub app-scoped
  em SettingsEndpoints vs impl user-scoped em SecretsEndpoints) — stub
  removido; GET list responde shape `[{name}]` que o client consome.
- **500s → 4xx**: `Guid.Parse` (pending-messages) → TryParse+400;
  `JsonElement` Null (git creds PUT, exec POST, ACP prompt) → guard+400;
  `/oauth/oidc/login` sem scheme → 404.
- **Testes**: `TestAuth` helper (dev-login cookie); 13 arquivos que
  dependiam de acesso anônimo migrados; 28 contract tests novos.
- **Nightly**: dev-login cookie + `ignored_auth`/`not_a_server_error`
  voltam a rodar (exclusões de fidelity debt seguem — issue #218 §3).

- [x] 0 `ignored_auth` não-justificado; 0 `not_a_server_error` (fixes).
- [ ] Nightly verde com checks completos — valida no próximo run.
