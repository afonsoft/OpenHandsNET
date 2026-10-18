# Billing e enforcement de planos (SPEC-20)

OpenHandsNET tem catálogo de planos, subscrições por organização e
metering de uso. **Enforcement vem desligado por default** — num
self-host OSS nada bloqueia; um operador liga com
`Billing:EnforcementEnabled=true` quando quiser aplicar cotas.

## Modelo

```
plans          → catálogo (name, limits_json, monthly_price_usd)
subscriptions  → org ↔ plan (status active/ended, started_at/ended_at)
usage_records  → tokens/custo agregados por org + período (yyyy-MM)
organizations  → plan_id (fallback quando não há subscrição ativa)
conversations  → org_id populado no create (fonte do escopo de quota)
```

Resolução do plano efetivo de uma org:

1. Subscrição `active` mais recente (não expirada) → plano dela.
2. Senão, `organizations.plan_id`.
3. Senão, plano `free` (seedado no startup, `{}` = sem limites).
4. Contexto sem org (uso pessoal) → `free`.

`LimitsJson` é JSON livre — campos desconhecidos são ignorados e JSON
inválido **falha aberto** (tratado como ilimitado + warning no log):

```json
{
  "max_conversations_per_day": 50,
  "max_concurrent_runs": 3,
  "max_tokens_per_month": 5000000,
  "max_sandbox_seconds_per_month": 86400,
  "max_sandbox_profile": "large"
}
```

`null`/ausente = ilimitado; negativos são tratados como ausentes.

## Onde o enforcement morde

| Ponto | Limite | Resposta |
|---|---|---|
| `POST /api/v1/app-conversations` | `max_conversations_per_day` | 403 `plan_limit_exceeded` |
| start-task worker (antes do sandbox) | `max_concurrent_runs`, `max_tokens_per_month`, `max_sandbox_seconds_per_month` | task falha com `plan_limit_exceeded:<key>` |

A violação **bloqueia o próximo start** — um run em andamento sempre
completa (metering acontece no fim do run).

Erro 403 (RFC 9457 + extensões):

```json
{
  "type": "about:blank", "status": 403,
  "detail": "plan_limit_exceeded",
  "limit_key": "max_conversations_per_day",
  "limit": 50, "used": 50
}
```

## Metering

- **Tokens/custo**: `IUsageMeter` faz upsert da linha `usage_records`
  `(org, yyyy-MM)` a cada `TokenEvent` de `CompletionEnd`.
- **Sandbox seconds / runs / sandboxes ativos**: derivados ao vivo de
  `sandboxes` + `conversations` por `IUsageService` — sem coluna extra.
- Leitura: `GET /api/v1/organizations/{id}/usage` (`perm:view_billing`).

## Admin API

| Endpoint | Permissão |
|---|---|
| `GET /api/v1/admin/plans` | `manage_super_admins` |
| `POST /api/v1/admin/plans` | `manage_super_admins` |
| `PATCH /api/v1/admin/plans/{id}` | `manage_super_admins` |
| `DELETE /api/v1/admin/plans/{id}` | `manage_super_admins` — **409** se referenciado por sub ativa ou org |
| `PUT /api/v1/admin/organizations/{org_id}/plan` | `manage_super_admins` — encerra a subscrição ativa e abre outra |
| `GET /api/v1/organizations/{id}/plan` | `view_billing` |

## Configuração

```bash
# .env / docker compose
OPENHANDSNET_Billing__EnforcementEnabled=true
```

```jsonc
// appsettings.json
"Billing": { "EnforcementEnabled": false }
```

## Cobrança (fronteira)

Não há gateway de pagamento embutido — `monthly_price_usd` é informativo
e a atribuição de plano é uma operação admin (cria subscrição `active` e
encerra a anterior, sem cobrar ninguém). Integração com Stripe/similares
fica como seam futuro via `IBillingProvider` — deliberadamente fora do
escopo OSS.

## BYOR — LiteLLM proxy (SPEC-50)

BYOR (bring-your-own-router) dá a cada usuário uma **virtual key** do
proxy LiteLLM com budget próprio — billing por usuário fica no proxy,
enquanto o metering interno (`usage_records`) continua ativo.

### Configuração

```bash
OPENHANDSNET_LlmProxy__BaseUrl=http://litellm:4000
OPENHANDSNET_LlmProxy__MasterKey=<secret — nunca em appsettings>
OPENHANDSNET_LlmProxy__Mode=hybrid      # off | hybrid | byor
OPENHANDSNET_LlmProxy__DefaultBudgetUsd=50
```

- `off` (default): BYOR desligado — zero mudança de comportamento.
- `hybrid`: catálogo OmniRoute local continua disponível; usuários com
  key ativa têm as chamadas roteadas ao proxy.
- `byor`: **strict** — só modelos da lista `permitted` do proxy; modelo
  fora da lista → `agent_diagnostic` `byor_model_rejected` + run `Error`.

### Endpoints (`/api/v1/keys/llm/byor*`)

| Endpoint | Comportamento |
|---|---|
| `GET /llm/byor` | `configured`, `key_prefix`, `budget_usd`, `models`, `created_at` — **nunca** a chave |
| `POST /llm/byor/refresh` | Revoga a anterior + cria nova — retorna a chave em claro **uma única vez** |
| `GET /llm/byor/permitted` | Modelos anunciados pelo proxy (`GET /models`, cache 60s) |
| `DELETE /llm/byor` | Revoga imediatamente (proxy + local) |

**Fail-closed**: proxy não configurado ou indisponível → **503**
`litellm_unavailable` (problem details) em todos os quatro endpoints —
nunca fallback silencioso para providers locais.

### Segurança

- `byor_keys` persiste **só metadados + ciphertext** (DataProtection +
  nonce, mesmo padrão do secret store): `user_id`, `org_id`, `key_id`,
  `key_prefix`, `budget_usd`, `models`, `created_at`, `revoked_at`.
- Uma key ativa por usuário — `refresh` revoga a anterior primeiro.
- A chamada LLM vai ao proxy com `Authorization: Bearer <virtual-key>`
  (em memória, decryptada só na borda) e `X-LiteLLM-Key: <key_id>` para
  tracking de spend no LiteLLM — o `key_id` **não é** a chave.
- Audit: `byor_key.refresh` / `byor_key.delete`.
- Master key só por env/secret; `LITELLM_MASTER_KEY` no compose
  enterprise (o mesmo valor em `LlmProxy:MasterKey`).

### Deploy

`docker-compose.enterprise.yaml` sobe `litellm` (imagem
`ghcr.io/berriai/litellm`) com `deploy/litellm.yaml` (model list de
exemplo + `master_key`/`database_url` por env) e um db `litellm`
criado por `deploy/postgres-init/`. Ver `enterprise.md`.
