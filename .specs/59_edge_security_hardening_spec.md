# SPEC — 59 · Edge Security: IP Allowlist + Input Guard + Per-Actor Rate Limit

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `edge-security-hardening` |
| Type | `Feature / Security` |
| Stack | `.NET 10 · middleware · LLM-lite screening` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Delivered` |
| Ticket | `TBD` |
| Depends on | `08` (auth), `33` (ISecurityAnalyzer), `57` (actors) |
| Reference | competitive-analysis §3.7 (G13/G20); docs.devin.ai
  `/enterprise/security-access/ip-access-lists`,
  `/enterprise/features/ai-guardrails` |
| Touches | `src/OpenHandsNET.{Application,Infrastructure,Api}`, `tests/` |

## 1. User Story

**As a** admin enterprise
**I want** restringir acesso à API/webapp por CIDR, screenar mensagens
de usuário e gatilhos externos contra prompt-injection/exfiltração, e
ter rate limit por ator
**So that** a superfície exposta fica defendida na borda como nos
enterprise do Devin (IP access lists + AI Guardrails). Hoje o
`ISecurityAnalyzer` só avalia **ações do agente**; o input do usuário
e de webhooks entra cru, e o rate limit atual (`ApiRateLimitMiddleware`)
não distingue atores nem permite allowlist.

## 2. Scope (RF)

### RF-001 — IP access lists

- Entidade `ip_access_rules`: `Id, Scope (global|org:{id}), Cidr,
  Action (allow|deny), Priority`.
- `EdgeAccessMiddleware` antes do auth: resolve IP (honra
  `X-Forwarded-For` só se `Proxy:TrustForwardedHeaders`), avalia
  deny-wins → allowlist-se-existe-allow → default allow.
- Endpoints admin `GET/POST/DELETE /api/v1/{orgs/{org}/}ip-rules`;
  paths isentos (`/health`, webhooks verificados por HMAC) via config
  `Edge:ExemptPaths`.
- Cache de regras 30 s (`IDistributedCache`).

### RF-002 — `IInputGuard` — screening de mensagens

- Interface `IInputGuard.ScoreAsync(text, channel)` →
  `{Verdict: allow|warn|block, Categories[], Reason}`.
- Implementações: `PatternInputGuard` (regexes/heurísticas de
  injection: "ignore previous instructions", exfil patterns, markup de
  controle) sempre ativa; `LlmInputGuard` opcional
  (`InputGuard:LlmRole` via OmniRoute — modelo barato, fail-open
  configurável).
- Aplicado em: `send-message`, `POST /app-conversations`
  (initial_message), webhooks chat/ticket/scm (SPEC-54/55/52 quando
  presentes), composer edit-resend.
- `block` → resposta educada ao usuário + evento `input_blocked` no
  stream + audit `guard.input_blocked` (categoria, não o conteúdo).
- Por-org tuning via `security_policies` (SPEC-48):
  `input_guard=off|pattern|llm`, `warn_threshold`.

### RF-003 — Rate limit por ator

- Evoluir `ApiRateLimitMiddleware`: chave
  `{actor_kind}:{actor_id}:{bucket}` (user/service_user/ip-anon),
  buckets por classe de endpoint (read/write/webhook/expensive).
- Limites por org/plano (`RateLimits` no plano — SPEC-20) com default
  sensato; resposta `429` + `Retry-After` + headers
  `X-RateLimit-{Limit,Remaining,Reset}`.
- `IDistributedCounter` (SPEC-36) p/ multi-réplica.

### RF-004 — Admin UI + docs

- Settings `Security → Edge`: rules table, input guard mode por org,
  rate-limit presets, test (`POST /api/v1/edge/check` — simula
  IP/mensagem e devolve decisão).
- Docs `docs/deployment/security.md` + seção edge.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET/POST/DELETE | `/api/v1/ip-rules` e `/api/v1/orgs/{org}/ip-rules` | admin |
| POST | `/api/v1/edge/check` | `{ip?, message?}` → decisão simulada |

Erros: `400` CIDR inválido · `403` ip_blocked/input_blocked · `429`.

## 4. Data model

`ip_access_rules`, `security_policies` + `InputGuardMode`,
`InputGuardLlmRole`. `audit_events` novos kinds. Migration.

## 5. NFR / Security

- Edge middleware é o 1º no pipeline; custo <1 ms em allow (cache).
- Guard nunca loga conteúdo da mensagem — só hash + categoria.
- `llm` guard tem timeout 2 s + fail-open|closed por política (default
  open — disponibilidade antes).
- `Edge:ExemptPaths` mínimo; webhook sig-verified continua verificado
  (allowlist não bypassa HMAC).

## 6. Task Plan

1. `ip_access_rules` + middleware + admin endpoints + exempt paths.
2. `IInputGuard` + Pattern guard + integração nos pontos de entrada.
3. Rate limit por ator + headers + counters distribuídos.
4. `edge/check` + admin UI + docs.

## 7. Deliverables

IP allowlist global/org, screening configurável de input, rate limit
por ator com headers padrão — três controles de borda auditáveis.

## 8. Referências

- `.specs/33_agent_hardening_spec.md` (`ISecurityAnalyzer`),
  `.specs/48_enterprise_security_hardening_spec.md` (policies),
  `.specs/36_distributed_cache_backplane_spec.md` (counters).
- docs.devin.ai ip-access-lists, ai-guardrails.

## 9. Guardrails

- Deny de webhook não pode ser criado por rota isenta — validação.
- Pattern rules versionadas em código (sem CRUD de regex por admin).
- `IInputGuard` nunca bloqueia `ask_user` interno do agente.

## 10. DoD

- IP fora de allowlist → 403; dentro → passa; deny > allow; exempt path
  livre.
- Mensagem com "ignore all previous instructions" → verdict ≥warn;
  política `llm`+closed com guard down → comportamento configurado.
- 60 req/min excedidas → 429 c/ headers corretos por ator.
- Testes unit do matcher CIDR + integração do guard. Suite verde;
  golden OpenAPI atualizado.


## 10. Delivered — evidências

- RF-001 `ip_access_rules` + `EdgeAccessMiddleware` (deny-wins,
  allow-required-if-any, cache 30s, `Proxy:TrustForwardedHeaders`,
  `Edge:ExemptPaths`) + admin endpoints
  `/api/v1/{orgs/{o}/}ip-rules`. Migrations SQLite+Postgres.
- RF-002 `IInputGuard` + `PatternInputGuard` (regex GeneratedRegex:
  injection/exfil/markup/act-as/base64) + `InputGuardService`
  (modo por org via `security_policies.input_guard`, LLM via
  `InputGuard:LlmEndpoint`, `FailMode` open|closed). Wired em
  send-message, initial_message e SlackChatOps (canal `webhook:slack`,
  `ChatOpsResult.Blocked`). Block → 403 `input_blocked` + audit
  `guard.input_blocked` (categorias + sha256:12, sem conteúdo).
- RF-003 `ApiRateLimitMiddleware` reescrito: key
  `{actor_kind}:{actor_id}:{bucket}:{minuto}`, buckets
  read/write/webhook/expensive, 429 + Retry-After + X-RateLimit-*.
- RF-004 página `/admin/security` (regras por escopo + edge/check),
  `POST /api/v1/edge/check`, esta seção no security.md.
- Divergências: cache de regras em `IMemoryCache` (spec dizia
  IDistributedCache — [A DEFINIR] para multi-instância); webhooks
  ticket/scm herdam o guard via canal `webhook:*` no send do prompt
  (SCM/ticket webhooks não geram prompt de usuário — cobertura via
  SlackChatOps e send-message).
- Testes: `Spec59EdgeTests` (24 casos: CIDR IPv4/6, patterns,
  classificação de bucket) — verdes.
