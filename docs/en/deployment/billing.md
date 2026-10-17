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
