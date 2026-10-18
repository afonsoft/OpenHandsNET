# Billing & plan enforcement (SPEC-20)

OpenHandsNET has a plan catalogue, per-org subscriptions and usage
metering. **Enforcement is off by default** — on OSS self-host nothing
blocks; an operator enables it with `Billing:EnforcementEnabled=true`
when quotas should apply.

## Model

```
plans          → catalogue (name, limits_json, monthly_price_usd)
subscriptions  → org ↔ plan (status active/ended, started_at/ended_at)
usage_records  → aggregated tokens/cost per org + period (yyyy-MM)
organizations  → plan_id (fallback when no active subscription)
conversations  → org_id populated on create (quota scope source)
```

Effective plan resolution for an org:

1. Most recent `active` (non-expired) subscription → its plan.
2. Otherwise, `organizations.plan_id`.
3. Otherwise, the `free` plan (seeded at startup, `{}` = no limits).
4. Org-less context (personal use) → `free`.

`LimitsJson` is free-form JSON — unknown fields are ignored and invalid
JSON **fails open** (treated as unlimited + log warning).

## Enforcement points

| Quota | Where | On exceed |
|---|---|---|
| `conversations_per_day` | `POST /app-conversations` | `402 quota_exceeded` |
| `max_concurrent_runs` | conversation start / automation fire | `409` / run `failed` |
| `tokens_per_month` | post-run metering | flag on next start |
| `sandbox_seconds` | sandbox lifecycle | conversation `failed` |

Automation fires hitting quota record `AutomationRun(failed,
error="quota: …")` — visible in run history.

## Usage

`GET /api/v1/orgs/{id}/usage` — tokens, cost and conversation counts for
the current period (admin/owner scope). `IUsageService` aggregates
after each run.

## BYOR / LiteLLM

Org-managed LLM credentials (bring-your-own-route) land in **SPEC-50**:
`byor_keys` persistence, virtual keys, permitted-models and rotation,
surfaced through `/api/v1/keys/llm/byor*` (403 stubs today).

## BYOR — LiteLLM proxy (SPEC-50)

BYOR (bring-your-own-router) gives each user a **LiteLLM virtual key**
with its own budget — per-user billing lives at the proxy while the
internal metering (`usage_records`) stays active.

### Configuration

```bash
OPENHANDSNET_LlmProxy__BaseUrl=http://litellm:4000
OPENHANDSNET_LlmProxy__MasterKey=<secret — never in appsettings>
OPENHANDSNET_LlmProxy__Mode=hybrid      # off | hybrid | byor
OPENHANDSNET_LlmProxy__DefaultBudgetUsd=50
```

- `off` (default): BYOR disabled — zero behavior change.
- `hybrid`: local OmniRoute catalog stays available; users with an
  active key route calls through the proxy.
- `byor`: **strict** — only proxy `permitted` models; a model outside
  the list → `agent_diagnostic` `byor_model_rejected` + run `Error`.

### Endpoints (`/api/v1/keys/llm/byor*`)

| Endpoint | Behavior |
|---|---|
| `GET /llm/byor` | `configured`, `key_prefix`, `budget_usd`, `models`, `created_at` — **never** the key |
| `POST /llm/byor/refresh` | Revokes the previous key + creates a new one — returns the cleartext key **exactly once** |
| `GET /llm/byor/permitted` | Models advertised by the proxy (`GET /models`, 60s cache) |
| `DELETE /llm/byor` | Revokes immediately (proxy + local) |

**Fail-closed**: unconfigured or unreachable proxy → **503**
`litellm_unavailable` (problem details) on all four endpoints — never a
silent fallback to local providers.

### Security

- `byor_keys` stores **metadata + ciphertext only** (DataProtection +
  nonce, same pattern as the secret store): `user_id`, `org_id`,
  `key_id`, `key_prefix`, `budget_usd`, `models`, `created_at`,
  `revoked_at`.
- One active key per user — `refresh` revokes the previous first.
- The LLM call hits the proxy with `Authorization: Bearer <virtual-key>`
  (in-memory, decrypted only at the edge) and `X-LiteLLM-Key: <key_id>`
  for spend tracking — the `key_id` is **not** the key.
- Audit: `byor_key.refresh` / `byor_key.delete`.
- Master key via env/secret only; `LITELLM_MASTER_KEY` in the
  enterprise compose (same value as `LlmProxy:MasterKey`).

### Deploy

`docker-compose.enterprise.yaml` brings up `litellm`
(`ghcr.io/berriai/litellm`) with `deploy/litellm.yaml` (example model
list + env-based `master_key`/`database_url`) and a `litellm` database
created by `deploy/postgres-init/`. See `enterprise.md`.
