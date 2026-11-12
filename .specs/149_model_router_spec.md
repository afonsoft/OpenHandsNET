# SPEC — 149 · Model Router (roteamento por role/custo/latência)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `model-router` |
| Type | `Feature / LLM gateway` |
| Stack | `.NET 10 · BYOK gateway (SPEC-134) · usage metering` |
| Repository | `afonsoft/sandcastle` |
| Status | `Draft` |
| Ticket | `GAP-MODEL-ROUTER` (docs/research/2026-11-competitive-gap-analysis.md §2.1 — OH Ent 0.103) |
| Depends on | `134` (BYOK/model picker), `20` (usage/quotas) |
| Reference | OpenHands Enterprise Model Routing (0.103.0) — conceitos apenas |
| Touches | `src/Sandcastle.{Application,Infrastructure,Api,Client}`, `tests/` |

## 1. User Story

**As a** org admin / usuário
**I want** que cada chamada de LLM seja roteada para o modelo certo
por role da tarefa (plan/code/review/analysis/embedding), custo e
latência — com fallbacks e política admin
**So that** paridade com o Model Routing do OpenHands Enterprise
0.103: hoje a conversa usa um único modelo escolhido no start; cada
agente de fundo (review/autofix/QA/wiki) já chama `role`-separado mas
sem roteamento inteligente.

## 2. Scope (RF)

### RF-001 — Routing model

- `IModelRouter.ResolveAsync(role, ctx)` → `{provider, model,
  api_key_ref, reason}` — ponto único por onde TODA chamada LLM
  passa (agent loop, critic, review, autofix, QA, wiki, scans,
  dedup, insights, embeddings).
- Roles existentes no código (verificar): `plan`, `code`, `review`,
  `analysis`, `chat`, `embedding`, `critic` — normalizar num enum
  `LlmRole` central; mapear callers atuais.
- `RoutingRules` (config + org settings): por role → ordered list de
  candidates `{provider, model, max_cost_per_1m?, weight?}` →
  seleção: primeiro disponível por (a) circuit fechado, (b) quota
  restante, (c) opcional round-robin por weight.

### RF-002 — Fallback chain + circuit breaker

- Provider falha (429/5xx/timeout `Llm:FailoverTimeout`) → próximo
  candidate da role com mark-do-not-retry por `Router:CircuitOpenSec`
  (default 60s); half-open probe após o cooldown.
- Fallback é auditado: `llm.route_fallback` event (from→to, reason).
- Sem candidate disponível → erro `503 llm_no_route` (nunca fallback
  silencioso para modelo arbitrário fora da lista).

### RF-003 — Budget-aware + admin policy

- Quando `remaining < X%` do budget mensal (SPEC-147 budget
  endpoint), candidates com `budget_tier: cheap` ganham prioridade
  (`Router:CheapWhenBelowPct=20`).
- Admin: por org define `allowed_models[]` (allowlist hard — router
  nunca resolve fora dela), `deny_models[]`, `default_role_map`.
- User: override por conversa (`model` no start-task continua
  funcionando — banca a role `code`/`chat` explicitamente, pinado;
  demais roles seguem router).

### RF-004 — Observabilidade

- `llm.route_decision` audit: role, chosen, candidates avaliados,
  reason, latency; `usage` rows já existentes ganham `role` coluna
  (dashboard by_model já existe — filtrar por role também).
- Settings > Usage: gráfico por role × modelo; Admin > Org: routing
  editor (lista ordenada por role, drag ou campo JSON validado por
  schema `routing_rules_v1`).

### RF-005 — Fora de escopo

- Roteamento por prompt-content classification (ML classifier) ·
  hedged requests (parallel race) · cost forecasting ·
  provider-side batch API — fase 2.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET | `/api/v1/orgs/{org}/routing-rules` | admin |
| PUT | `/api/v1/orgs/{org}/routing-rules` | `{rules: routing_rules_v1}` |
| GET | `/api/v1/llm/routes` | catálogo provider×model disponíveis |
| GET | `/api/v1/usage/llm?by=role` | breakdown role×model |

## 4. Data model

- `org_settings`/`user_settings` + `routing_rules_json` (kv store
  existente — sem tabela nova se couber); `usage` + `role` coluna
  (migration dupla — verificar se já existe).

## 5. NFR / Security

- Allowlist admin é hard-boundary: router valida candidato contra
  `allowed_models` antes de qualquer chamada.
- Fallbacks não cruzam trust boundary de dados (provider externo →
  interno proibido quando `DataResidency=internal-only`).
- Circuit state in-memory por worker; não persiste (fail-open após
  restart é aceitável).
- Nenhuma API key nova — reusa BYOK/secret refs existentes.

## 6. Task Plan

1. `LlmRole` enum + mapear callers + `IModelRouter` default
   pass-through (compat).
2. `RoutingRules` model + resolver + circuit breaker + fallback
   events.
3. Budget-aware ordering + allowlist enforcement.
4. Admin UI routing editor + usage by role.
5. Testes: unit (resolver order, circuit, budget tier, allowlist
   deny, fallback audit), integração (fake providers falham→next),
   golden OpenAPI.

## 7. Deliverables

Todo LLM call passa por um router auditável com fallback e política
por role/custo — a camada que separa "gateway BYOK" de "router".

## 8. Referências

- `docs/research/2026-11-competitive-gap-analysis.md` §2.1 (OH
  0.103 Model Routing).
- `docs/specs/134_beta_byok_spec.md` (gateway),
  `20_*` (quotas/usage).

## 9. Guardrails

- Default = pass-through do comportamento atual (router off ou
  regra vazia → modelo escolhido no start-task) — zero regressão.
- Nunca vazia a allowlist em erro ao usuário não-admin.
- `role` deve propagar até `usage` rows — relatórios de custo por
  workflow dependem disso.

## 10. DoD

- Provider falha → fallback automático + audit event + run continua.
- Allowlist nega modelo não-listado (403/503 auditado).
- Budget <20% → cheap tier priorizado (teste unit).
- Compat: sem routing rules, comportamento idêntico ao atual.
- Suite verde; migration dupla; golden; i18n ×8.
