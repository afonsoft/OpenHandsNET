# SPEC — 50 · BYOR LiteLLM (Bring-Your-Own-Router)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `byor-litellm` |
| Type | `Feature / Infrastructure + API` |
| Stack | `.NET 10 · LiteLLM admin API · OmniRoute` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` |
| Ticket | [#116](https://github.com/afonsoft/OpenHandsNET/issues/116) |
| Depends on | `08` (api keys/RBAC), `23` (OmniRoute catalog + IModelRouter), `20` (plan quotas) |
| Reference | upstream Cloud docs (conceitos): LiteLLM proxy + virtual keys; `OpenHands/OpenHands-Cloud` (Polyform — conceitos apenas) |
| Touches | `src/OpenHandsNET.Infrastructure/Identity`, `src/OpenHandsNET.Api/Endpoints/KeysEndpoints.cs`, `deploy/litellm.yaml`, `docs/deployment/` |

## 1. User Story

**As a** admin enterprise que já opera um LiteLLM proxy
**I want** o OpenHandsNET emitindo virtual keys por usuário/org com
budget limits e respeitando a lista de modelos permitidos
**So that** consumo de LLM passa pelo router da empresa (auditoria +
budget + modelos homologados) — hoje os 4 endpoints
`/api/v1/keys/llm/byor*` são stubs `403` e `deploy/litellm.yaml` é um
placeholder de 9 linhas.

**Contexto:** upstream Cloud usa LiteLLM proxy + virtual keys por
usuário; OmniRoute (SPEC-23) já abstrai provider/modelo por role — o
BYOR entra como **fonte adicional de credencial+endpoint** no
`IModelRouter`, não como router paralelo. Sem LiteLLM configurado,
tudo segue igual (config atual de providers continua default).

## 2. Scope (RF)

### RF-001 — `ILiteLlmAdminClient` (infra)

- Client HTTP tipado para o LiteLLM admin API
  (`LlmProxy:BaseUrl`, `LlmProxy:MasterKey` via secret/env — nunca em
  config commitada):
  - `CreateVirtualKeyAsync(userId, orgId, budget, models)` → `{key,
    budget_id}`.
  - `UpdateKeyBudgetAsync(keyId, budget)`, `DeleteKeyAsync(keyId)`.
  - `ListModelsAsync()` → modelos que o proxy serve.
- Fail-closed: proxy indisponível → endpoints BYOR respondem `503`
  problem-details (`litellm_unavailable`), nunca fallback silencioso.

### RF-002 — Implementar os 4 stubs

- `GET /api/v1/keys/llm/byor` → `{configured, key_prefix, budget,
    models}` da key BYOR do usuário corrente (nunca o key inteiro).
- `POST /api/v1/keys/llm/byor/refresh` → rotaciona: revoga key velha,
  cria nova, retorna `{key}` **uma vez** + novo prefix.
- `GET /api/v1/keys/llm/byor/permitted` → lista de modelos
  permitidos no proxy (para o `ModelSelector` filtrar).
- `DELETE /api/v1/keys/llm/byor` → revoga a virtual key.
- Registro `ByorKeyRecord` (`{Id, UserId, OrgId, KeyId (id no proxy),
  KeyPrefix, BudgetUsd, CreatedAt, RevokedAt}`) — migration ×2; só
  metadados, nunca o key em claro.

### RF-003 — Integração com OmniRoute

- `IModelRouter.SelectAsync` consulta primeiro a BYOR key ativa do
  owner (quando `LlmProxy:Mode = byor|hybrid`); o model só pode sair
  da lista `permitted` → violação → `agent_diagnostic` +
  `PlanLimit`/400 na start (fail-closed).
- `hybrid` (default quando proxy configurado): catálogo OmniRoute
  local continua disponível; `byor` estrito: só modelos do proxy.
- Billing/usage: o header `X-LiteLLM-Key` vai na request ao proxy —
  usage segue contando no `IUsageService` interno normalmente.

### RF-004 — Deploy + docs

- `deploy/litellm.yaml` real: LiteLLM com postgres próprio,
  `master_key` via env, config de modelos exemplo + budget default.
- Compose profile `litellm` (`--profile enterprise`) + envs
  documentadas.
- `docs/deployment/billing.md`/`enterprise.md`: seção BYOR — setup,
  rotate, permitted models, fail-closed.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET | `/api/v1/keys/llm/byor` | `{configured,key_prefix,budget,models}` |
| POST | `/api/v1/keys/llm/byor/refresh` | rotação → `{key}` 1× |
| GET | `/api/v1/keys/llm/byor/permitted` | modelos permitidos |
| DELETE | `/api/v1/keys/llm/byor` | revoga virtual key |

## 4. Data model

1 tabela `byor_keys` (UNIQUE `user_id` onde não revogado) — migration
nos dois contextos.

## 5. NFR / Security

- Virtual key em claro **uma vez** (no refresh) — persistido só
  `key_id` + `key_prefix`.
- `MasterKey` via env/secret — nunca em `appsettings` commitado.
- Proxy down → `503`; modelo fora de `permitted` → rejeitado na borda.
- Delete da key revoga imediatamente (invalida cache do router).

## 6. Task Plan

1. `ILiteLlmAdminClient` + `ByorKeyRecord` + migration ×2.
2. Endpoints BYOR (substitui stubs) — contract tests com fake handler.
3. OmniRoute BYOR mode + `permitted` enforcement — unit tests.
4. `deploy/litellm.yaml` + compose profile + docs.
5. ModelSelector filtra por `permitted` quando BYOR ativo.

## 7. Deliverables

`ILiteLlmAdminClient`, `byor_keys`, 4 endpoints reais, router mode,
deploy profile, docs, testes.

## 8. Referências

- `docs/specs/23` (OmniRoute), `docs/specs/20` (quotas), `docs/specs/32`
  (LiteLLM proxy no cloud upstream).
- LiteLLM admin API docs (`/key/generate`, `/key/delete`,
  `/model/info`).

## 9. Guardrails

- Upstream cloud é Polyform — conceitos apenas.
- Key nunca em log/event store; master key nunca em config commitada.
- Sem proxy configurado → comportamento atual inalterado (opt-in).

## 10. DoD

- 4 endpoints respondem (não mais 403) com LiteLLM fake.
- Refresh rotaciona e antiga morre (delete chamado no proxy).
- Modelo fora de `permitted` rejeitado fail-closed.
- Suite verde; SPEC `Done`; docs atualizadas.
